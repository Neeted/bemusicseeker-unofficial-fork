# P0-04 .NET 10 移行準備と依存関係整理計画

[総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md)

## 目的

現時点の BeMusicSeeker は `net472` / WPF / WinForms / HintPath DLL / native DLL / `app.config` / `System.Configuration` が混在している。`.NET 10` へ移行するには、TFM を変える前に依存関係と runtime 境界を整理する必要がある。

この計画は「すぐに `net10.0-windows` へ本移行する」計画ではない。まず移行阻害要因を潰し、dry-run と blocker report を作れる状態にする。

## 公式資料メモ

- .NET supported versions: https://learn.microsoft.com/dotnet/core/releases-and-support
- WPF upgrade overview: https://learn.microsoft.com/dotnet/desktop/wpf/migration/
- .NET 10 breaking changes: https://learn.microsoft.com/dotnet/core/compatibility/10
- .NET project SDK overview: https://learn.microsoft.com/dotnet/core/project-sdk/overview
- NuGet packages.config to PackageReference: https://learn.microsoft.com/nuget/consume-packages/migrate-packages-config-to-package-reference

## 現状観測

| 項目 | 現状 |
|---|---|
| app project | `BeMusicSeeker.csproj`, `Sdk="Microsoft.NET.Sdk.WindowsDesktop"`, `TargetFramework=net472`, `UseWPF=True`, `UseWindowsForms=True` |
| test project | `BeMusicSeeker.Tests.csproj`, `TargetFramework=net472` |
| updater | `BeMusicSeeker.Updater.csproj`, `TargetFramework=net472` |
| packages | `PackageReference` 使用。`packages.config` は見当たらない |
| direct DLL refs | `libs/*.dll` に `Livet`, `MetroRadiance`, `System.Windows.Interactivity`, `Bass.Net`, `sqlite.net`, `DynamicJson`, `SevenZipExtractor`, `Microsoft.WindowsAPICodePack` など |
| native DLL | `native/Everything3_x64.dll`, `native/EverythingBridge_x64.dll`, `vendor/native/x64/*.dll` |
| config | root `app.config` に userSettings, probing privatePath, AppContextSwitchOverrides |
| risk | WPF+WinForms 同時参照、MenuItem/ContextMenu ambiguity、System.Configuration、assembly probing、native load path、古い UI behavior libraries |

## 現在地と即時棚卸し範囲

2026-07-06 時点では `global.json` で .NET SDK 10.0.301 を使う開発環境に整備済みだが、app / tests / updater の TFM はすべて `net472` のままである。つまり「SDK 10 で .NET Framework アプリを開発している」状態であり、runtime として .NET 10 へ移行済みではない。

即並行できるもの:

- TFM を変更しない dependency inventory。
- `BeMusicSeeker.csproj` / tests / updater / tools の `TargetFramework`, `UseWPF`, `UseWindowsForms`, `HintPath`, native DLL copy, `System.Configuration`, `System.Deployment` 参照の棚卸し。
- `app.config` の probing `privatePath`, userSettings, AppContextSwitchOverrides の棚卸し。
- `Settings.Default` / `System.Configuration` 直接参照の境界調査。
- WPF + WinForms 混在による `MenuItem` / `ContextMenu` ambiguity 調査。
- `libs/`, `native/`, `vendor/native/x64/` の load / copy / relocation policy の台帳化。

後続判断に回すもの:

- `TargetFramework` を `net10.0-windows` に変える本移行。
- `app.config` probing を `.deps.json` / runtimeconfig / copy policy へ置き換える設計。
- `Settings.Default` / user.config migration。
- WindowsFormsHost / WebBrowser / native audio / external player host の runtime QA。

これらは P0-01 / P0-03 の UI 境界、P0-02 の domain facade 境界、P0-04 Phase 0 の inventory が揃った後に詳細な実装プランを検討する。

## 移行方針

1. まず `net472` baseline を安定させる。
2. dependency inventory と blocker report を作る。
3. UI / domain 巨大クラスの分割で依存境界を狭める。
4. `net10.0-windows` dry-run branch を作り、build failure を分類する。
5. 本移行は別計画にする。P0-01〜03 が終わる前に全 TFM 変更を本線へ入れない。

## Phase 0: 棚卸し

### Ticket NET10-0A: dependency inventory を更新する

既存候補:

- `.tmp/deps/dependency-inventory.md`
- `.tmp/deps/dependency-licenses.md`
- `.tmp/deps/dependency-policy.md`

作業:

1. `BeMusicSeeker.csproj`, `BeMusicSeeker.Tests.csproj`, `BeMusicSeeker.Updater.csproj`, `tools/**/*.csproj` の PackageReference / Reference / HintPath / Native copy を一覧化する。
2. 各 dependency を分類する。
   - NuGet で更新可能
   - HintPath 固定が必要
   - native asset を含む
   - .NET Framework 専用の可能性
   - WPF / WinForms UI library
   - ライセンス確認が必要
3. `.tmp/deps/dotnet10-blocker-inventory.md` を追加する。

受け入れ条件:

- 依存ごとに「net10 で置換候補を調べる」「retain as-is」「wrapper 化する」「不明」が分かる。
- version を決め打ちしない。実際の package update は個別 ticket にする。

### Ticket NET10-0B: API / namespace risk inventory を作る

対象:

- `System.Configuration`
- `System.Deployment`
- `Microsoft.VisualBasic`
- `System.Windows.Forms`
- `WindowsFormsIntegration`
- `System.Drawing`
- `Microsoft.WindowsAPICodePack`
- P/Invoke / native DLL load
- `AppDomain`, CAS, remoting 相当があれば記録

作業:

```powershell
rg "System\.Configuration|ConfigurationManager|Settings\.Default|System\.Deployment|WindowsFormsHost|System\.Windows\.Forms|System\.Drawing|DllImport|LoadLibrary|AppDomain|Evidence|PermissionSet|SecurityPermission" .\BeMusicSeeker .\BeMusicSeeker.Tests
```

受け入れ条件:

- `.tmp/dotnet10/api-risk-inventory.md` がある。
- どの P0/P1 計画で狭めるかが書かれている。

## Phase 1: source compatibility を先に改善する

### Ticket NET10-1A: WPF / WinForms ambiguous type を明示する

`.NET 10` では WPF と WinForms の両方を参照する app で `MenuItem` / `ContextMenu` の曖昧さが source incompatible になる可能性がある。BeMusicSeeker は `UseWPF=True` かつ `UseWindowsForms=True` なので、今のうちに曖昧な型名を明示する。

作業:

1. `MenuItem`, `ContextMenu` の C# 型参照を `rg` で洗う。
2. WPF の型なら `System.Windows.Controls.MenuItem` / `System.Windows.Controls.ContextMenu` を明示する。
3. WinForms の型なら `System.Windows.Forms.MenuItem` 等を明示する。
4. XAML は XML namespace で曖昧でなければ基本触らない。

受け入れ条件:

- C# で曖昧な `MenuItem` / `ContextMenu` 型参照が残らない。
- behavior は変えない。

### Ticket NET10-1B: `System.Configuration` / `Settings.Default` 依存を wrapper へ寄せる

関連計画:

- [Settings / テスト基盤 / 境界整備計画](./P1-03_Settings_テスト基盤_境界整備計画.md)

作業:

1. `IAppSettingsStore` / `AppSettingsSnapshot` を導入する。
2. 新規 service には `Settings.Default` を直接渡さない。
3. P0-01 / P0-02 / P0-03 で抽出する service は snapshot を受け取る。
4. 既存 app.config / user.config の互換は維持する。

受け入れ条件:

- 新規 service で `Settings.Default` 参照が増えない。
- settings migration を later に切れる。

### Ticket NET10-1C: direct MessageBox / dialog / Window 依存を route 経由へ寄せる

既存に `Views/Dialogs/UiDialogCoordinator.cs` などがあるため、これを強化する。

作業:

1. source-text tests が禁止している direct dialog pattern を確認する。
2. `BMSLibrary`, `BMSPlaylist`, ViewModel service から WPF `MessageBox` を直接呼ばない。
3. View が必要な dialog request を受けて coordinator へ流す。

受け入れ条件:

- domain service が WPF dialog API に依存しない。
- `.NET 10` 移行時の UI/API 問題が View 層に限定される。

## Phase 2: project file / build layout を近代化しやすくする

### Ticket NET10-2A: project SDK の切替可能性を検証する

現状は `Microsoft.NET.Sdk.WindowsDesktop`。modern .NET の WPF/WinForms では `Microsoft.NET.Sdk` と `UseWPF` / `UseWindowsForms` の組み合わせを使う方向になる。

作業:

1. branch 上で `BeMusicSeeker.csproj` の SDK を `Microsoft.NET.Sdk` に変えて `net472` build が通るか検証する。
2. 通らない場合はエラーを記録し、本線に入れない。
3. 通る場合のみ差分を最小化して本線採用を検討する。
4. `GenerateResourceUsePreserializedResources`, resource, app.manifest, WPF compile item に影響がないか確認する。

受け入れ条件:

- `net472` baseline を壊さない。
- SDK 切替可否が report に残る。

### Ticket NET10-2B: native / managed dependency copy layout を document 化する

対象:

- `RelocateManagedDependenciesToLibs`
- `RemoveLegacyNativeOutputArtifacts`
- `CopyUpdaterToAppOutput`
- `native/*.dll`
- `vendor/native/x64/*.dll`

作業:

1. output layout を `.tmp/dotnet10/output-layout.md` に図示する。
2. assembly probing と native DLL search path の依存を記録する。
3. `net10` で `app.config` probing が使えない/弱くなる場合の代替案を記録する。
4. 本実装は P0-04 後続 ticket に分ける。

受け入れ条件:

- output layout を変えずに説明できる。
- migration branch で copy failure を調査しやすい。

### Ticket NET10-2C: old UI behavior libraries の置換候補を調査する

対象例:

- `System.Windows.Interactivity.dll`
- `Microsoft.Expression.Interactions.dll`
- `Microsoft.Expression.Drawing.dll`
- `Microsoft.Expression.Effects.dll`
- `MetroRadiance*`
- `QuickConverter`
- `Livet` / `Livet.Extensions`

作業:

1. 各 assembly の使用箇所を `rg` で調査する。
2. 置換候補、維持理由、削除可能性を dependency inventory に追記する。
3. すぐに置換しない。使用箇所を wrapper / XAML resource で局所化できるか評価する。

受け入れ条件:

- 置換対象の優先順位が分かる。
- UI split 後に置換しやすい単位が分かる。

## Phase 3: `net10.0-windows` dry-run branch

この phase は P0-01〜03 の主要境界整理後に実行する。

### Ticket NET10-3A: dry-run target を追加して blocker report を作る

作業案:

1. 作業 branch を切る。
2. `TargetFramework` を一時的に `net10.0-windows` へ変更する。必要なら `UseWPF`, `UseWindowsForms`, `EnableWindowsTargeting` を設定する。
3. `dotnet restore` / `dotnet build` を実行する。
4. build error を次に分類する。
   - project SDK / MSBuild item
   - missing framework API
   - dependency binary incompatibility
   - WPF/WinForms source ambiguity
   - System.Configuration/settings
   - native asset / output layout
   - language/analyzer/style
5. `.tmp/dotnet10/dry-run-blockers.md` に記録する。
6. dry-run だけで終える場合は本線へ TFM 変更を入れない。

受け入れ条件:

- `.NET 10` 本移行計画を切れるだけの blocker list がある。
- blocker ごとに owner plan が紐づく。

## 完了目標

- `net472` baseline を維持しながら `.NET 10` 移行阻害要因が可視化される。
- WPF+WinForms 由来の source incompatibility を先に潰せる。
- direct DLL / native DLL / config / settings のリスクが台帳化される。
- 本移行は「P0-01〜03 完了後に詳細な実装プランを検討する」と明確化されている。
