# Architecture

この資料は現行実装の大枠を示す。詳細な処理境界は機能別仕様を正本にする。

## レイヤ

- `BeMusicSeeker.Views`
  - WPF 画面。
  - ユーザー操作を ViewModel へ渡す。
- `BeMusicSeeker.ViewModels`
  - UI state と use case orchestration。
  - 中心は `MainWindowViewModel`。
- `BeMusicSeeker.Models`
  - library catalog、playlist、install、resource health、keyword search などの domain logic。
  - 中心は `BMSLibrary` と `BMSPlaylist`。
- `BeMusicSeeker.Models.BmsLibraryInternal`
  - `BMSLibrary` の startup、DB access、install estimation、file operation などを分割した service 群。
- `BeMusicSeeker.Models.Utils`
  - Everything bridge wrapper、scan helper、hash helper、settings / utility。
- `native/EverythingBridge`
  - Everything SDK 3 を使う native bridge。
  - C# 側は bridge DLL を同一ビルド成果物として扱う。

## 主要 Component

- `App`
  - process startup、settings upgrade、logging、global exception handling。
- `MainWindowViewModel`
  - startup / reload / install / playlist operation の入口。
  - startup background scheduler と progress 表示を管理する。
- `BMSLibrary`
  - 所持 catalog、pending package、resource index、score snapshot、install operation の正本。
- `BMSPlaylist`
  - table header / playlist entries / external playlist sync。
- `BmsLibraryDbGateway`
  - song DB / score DB access の gateway。
  - startup hydration の read phase は read-only connection、write は明示 transaction path を使う。
- `EverythingNative`
  - native bridge result を decode し、native scan path では resource dictionaries を materialize せず `LibraryResourceIndex` / `DirectoryResourceLookupCache` へ渡す。

## Startup Boundary

startup は次を分けて扱う。

- install readiness
  - catalog、destination resource index、pending package state が揃った状態。
- UI operable
  - startup UI refresh が終わり、操作可能になった状態。
- initialization complete
  - startup background task まで完了した状態。

詳細は [startup-initialization-flow.md](startup-initialization-flow.md) を参照する。

## Native Bridge Policy

Everything が使える場合、通常起動の file enumeration は `EBridge_ScanChartAndResources` を使う。

- chart / audio / image / movie を列挙する。
- chart-relative resource key と reverse lookup surface を native packed result に含める。
- managed 側で旧 basename-only / all-resource surface を main path に戻さない。

Everything が使えない場合は managed fallback scan を使う。ただし、古い native ABI や contract mismatch への互換 fallback は行わない。

## Documentation Priority

古い計画資料より `spec/` 配下の現行仕様を優先する。経緯は `../plan/` を参照する。
