# P0-04 .NET 10 移行準備と依存関係整理計画

[総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md) / [PLAN_STATUS](./PLAN_STATUS.md)

## 目的

現時点の BeMusicSeeker は `net472` / WPF / WinForms / HintPath DLL / native DLL / `app.config` / `System.Configuration` が混在している。

Refactoring MVP Gate 通過前に `net10.0-windows` への本移行は行わない。P0-04 / Lane D は、TFM を変えず、blocker inventory、adapter / gateway 化、dependency cleanup、migration readiness の明文化に限定する。

## 現状観測

| 項目 | 現状 |
|---|---|
| app project | `BeMusicSeeker.csproj`, `Sdk="Microsoft.NET.Sdk.WindowsDesktop"`, `TargetFramework=net472`, `UseWPF=True`, `UseWindowsForms=True` |
| test project | `BeMusicSeeker.Tests.csproj`, `TargetFramework=net472` |
| updater | `BeMusicSeeker.Updater.csproj`, `TargetFramework=net472` |
| direct DLL refs | `libs/*.dll` に `Livet`, `MetroRadiance`, `System.Windows.Interactivity`, `Bass.Net`, `sqlite.net`, `DynamicJson`, `SevenZipExtractor`, `Microsoft.WindowsAPICodePack` など |
| native DLL | `native/Everything3_x64.dll`, `native/EverythingBridge_x64.dll`, `vendor/native/x64/*.dll` |
| config | root `app.config` に userSettings, probing privatePath, AppContextSwitchOverrides |
| risk | WPF + WinForms 同時参照、`MenuItem` / `ContextMenu` ambiguity、System.Configuration、assembly probing、native load path、古い UI behavior libraries |

## Active Ticket: `REF-MVP-D1`

### .NET 10 blocker inventory in devdocs

目的:

- TFM は変更しない。
- `Settings.Default`、`System.Configuration`、`app.config`、HintPath DLL、native DLL、WPF + WinForms、P/Invoke、external process host の blocker inventory を `.tmp` ではなく `devdocs` 管理下に置く。
- .NET 10 本移行ではなく、Refactoring MVP Gate のための blocker 可視化に限定する。

主対象:

- `BeMusicSeeker.csproj`
- `BeMusicSeeker.Tests/BeMusicSeeker.Tests.csproj`
- `BeMusicSeeker.Updater/BeMusicSeeker.Updater.csproj`
- `app.config`
- `libs/`
- `native/`
- `vendor/native/x64/`
- inventory: `devdocs/plan/BeMusicSeeker_refactoring_plans/inventory/REF-MVP-D1_dotnet10_blockers.md`

調査コマンド例:

```powershell
rg "System\.Configuration|ConfigurationManager|Settings\.Default|System\.Deployment|WindowsFormsHost|System\.Windows\.Forms|System\.Drawing|DllImport|LoadLibrary|AppDomain|Evidence|PermissionSet|SecurityPermission" .\BeMusicSeeker .\BeMusicSeeker.Tests
```

完了条件:

- blocker inventory が git 管理下にある。
- 各 blocker について、該当箇所、影響、refactor 前にできる対処、.NET 10 移行時に検討する対処が書かれている。
- TFM 変更や release 作業をしていない。
- docs のリンクが壊れていない。
- `git diff --check` が通る。

## Migration Readiness Scope

対象:

- dependency inventory。
- `Settings.Default` / `System.Configuration` / `app.config` の境界整理。
- HintPath DLL / native DLL / output layout の台帳化。
- WPF + WinForms ambiguity の source compatibility 調査。
- P/Invoke / external process host の adapter / gateway 化候補整理。

対象外:

- `TargetFramework` を `net10.0-windows` に変える本移行。
- release / publish script の運用変更。
- version / release notes 更新。
- user.config migration の実装。
- native / managed dependency の置換決定。

## 後続候補

`REF-MVP-D1` 完了後に、次を Refactoring MVP の進捗に合わせて選ぶ。

- WPF / WinForms ambiguous type を明示する。
- `Settings.Default` / `System.Configuration` 依存を wrapper / options snapshot へ寄せる。
- direct MessageBox / dialog / Window 依存を route 経由へ寄せる。
- native / managed dependency copy layout を document 化する。
- old UI behavior libraries の置換候補を調査する。

## 本移行の扱い

`net10.0-windows` dry-run branch は、Lane A / B / C の主要境界整理と Lane D inventory が揃った後に別計画として行う。本線へ TFM 変更を入れるのは Refactoring MVP Gate 通過後に再判断する。
