# REF-MVP-D1 .NET 10 blocker inventory

[P0-04 へ戻る](../P0-04_DotNet10_移行準備と依存関係整理計画.md) / [PLAN_STATUS](../PLAN_STATUS.md)

最終更新日: 2026-07-07

## Scope

この inventory は `net10.0-windows` 本移行を行うものではない。現行の `net472` baseline を維持したまま、.NET 10 移行時に blocker になり得る依存と、Refactoring MVP 中に先行できる境界整理を記録する。

調査時点:

- `BeMusicSeeker.csproj`: `TargetFramework=net472`, `UseWPF=True`, `UseWindowsForms=True`, `PlatformTarget=x64`
- `BeMusicSeeker.Tests/BeMusicSeeker.Tests.csproj`: `TargetFramework=net472`, `PlatformTarget=x64`
- `BeMusicSeeker.Updater/BeMusicSeeker.Updater.csproj`: `TargetFramework=net472`, `PlatformTarget=x64`
- `app.config`: `userSettings` 78 件、`probing privatePath="libs"`、`AppContextSwitchOverrides`
- `libs/*.dll`: 20 managed DLL
- `native/*.dll`: 2 DLL
- `vendor/native/x64/*.dll`: 9 DLL

## Blockers

| ID | Risk | 該当箇所 | 影響 | Refactoring MVP 中にできる対処 | .NET 10 移行時に検討する対処 |
|---|---|---|---|---|---|
| D1-01 | `System.Configuration` / `Settings.Default` | `BeMusicSeeker/Properties/Settings.cs`, `PortableSettingsProvider.cs`, `app.config`, `App.cs`, `MainWindowViewModel.cs`, `MainWindowViewModel.SettingDialogViewModel.cs`, `BMSLibrary.cs`, `BMSPlaylist*.cs`, tests など。`Settings.Default` は 32 ファイル / 2106 occurrence (`rg -o "Settings\\.Default"`) | .NET Framework の userSettings / provider / upgrade 前提が application state、domain、tests に広く漏れている。TFM 変更時に user.config migration、save timing、test isolation が同時に壊れやすい | 新しい機能や抽出 service へ `Settings.Default` を直接持ち込まない。`BmsLibraryOptionsSnapshot` のような snapshot / gateway を優先し、既存設定の読み書き境界を workflow 単位で寄せる | `System.Configuration.ConfigurationManager` package 継続、独自 settings store、options snapshot のどれを採用するか decision record を作る。既存 user.config の移行手順と互換性テストを別 ticket 化する |
| D1-02 | `app.config` probing / custom output layout | `app.config`: `supportedRuntime`, `AppContextSwitchOverrides`, `probing privatePath="libs"`, `userSettings`; `BeMusicSeeker.csproj`: `RelocateManagedDependenciesToLibs`, `RemoveLegacyNativeOutputArtifacts` | .NET Core / .NET 10 では assembly probing と .NET Framework runtime 設定の扱いが変わる。現在は managed DLL を build 後に `libs` へ移して元を削除し、native DLL も独自に移動・削除しているため、publish / deps.json / test output と衝突しやすい | `libs` 配置と native copy layout を inventory に固定し、コード側に暗黙の probing 依存を増やさない。`RelocateManagedDependenciesToLibs` に依存する DLL と通常 CopyLocal でよい DLL を分ける | `runtimeconfig.json` / apphost / publish layout で必要な設定へ置換する。`probing` 依存は `CopyLocal` / deps.json / RID native asset / explicit load のいずれかへ移す |
| D1-03 | HintPath managed DLL | `BeMusicSeeker.csproj`: `Livet`, `Livet.Extensions`, `IniLibrary`, `System.Windows.Interactivity`, `Microsoft.Expression.*`, `MetroRadiance*`, `Bass.Net`, `sqlite.net`, `DynamicJson`, `SevenZipExtractor`, `Microsoft.WindowsAPICodePack*`, `QuickConverter`, `SgmlReaderDll`, `System.Collections.Immutable`, `OggVorbis.NET64`。Tests も `..\libs` を参照 | NuGet 管理外の DLL が target framework compatibility、CopyLocal、binding、license / replacement decision の blocker になる | 参照の追加・削除を ad hoc に行わない。抽出 service では DLL 型を public contract に出さない。replacement が必要な library は P0 後続候補に分ける | 各 DLL の .NET 10 compatibility と置換先を調査する。可能なものは NuGet 化、古い UI behavior 系は `Microsoft.Xaml.Behaviors.Wpf` 等への置換可否を別計画で検討する |
| D1-04 | Native DLL / x64 output layout | `native/Everything3_x64.dll`, `native/EverythingBridge_x64.dll`, `vendor/native/x64/7z.dll`, `sqlite3.dll`, `bass*.dll`, `OggVorbis.NET64.dll`; `BeMusicSeeker.csproj` copies vendor native DLLs to `libs/x64`, tests copy `7z.dll` to `libs/x64` and `sqlite3.dll` to `x64` | app と tests で native DLL 配置が揃っていない。`AppDomain.CurrentDomain.BaseDirectory` 前提の load path が publish / single-file / test runner で壊れやすい | native load path を inventory に残し、これ以上 call site ごとの path 組み立てを増やさない。wrapper service へ寄せる際は explicit base directory を受け取れる形にする | `runtimes/win-x64/native` 風の layout、明示 copy、RID publish、test output の統一を検討する。single-file 対応は別判断にする |
| D1-05 | P/Invoke / manual native load / CAS attributes | `DllLoader.cs`, `EverythingNative.cs`, `FastDirectoryEnumerator.cs`, `ExplorerOpenService.cs`, `Ribbit/Windows/*.cs`, `Ribbit/Media/Audio/BassNet.cs`; `DllImport` は app 内 21 箇所に加え Ribbit 側にも存在。`SecurityPermission`, `ReliabilityContract`, `SuppressUnmanagedCodeSecurity`, `unsafe` も残る | Windows 専用 API と manual load が散在し、TFM 変更時に analyzer / trimming / publish layout / platform guard / CAS attribute の確認が必要 | Native interop を新規 workflow に直書きしない。既存 wrapper (`EverythingNative`, `ExplorerOpenService`, `FastDirectoryEnumerator`, Ribbit audio/window helpers) の外へ P/Invoke を漏らさない | `[SupportedOSPlatform("windows")]` や platform guard、source-generated interop の採否を検討する。CAS attribute の扱いと manual `LoadLibrary` の loader policy / error reporting を決める |
| D1-06 | WPF + WinForms + WebBrowser / COM + System.Drawing 混在 | `BeMusicSeeker.csproj`: `UseWPF=True`, `UseWindowsForms=True`, `WindowsFormsIntegration`; `MainWindow.xaml`: `WindowsFormsHost`, `WebBrowser`; `MainWindow.cs`: `AxIWebBrowser2` private reflection; `App.cs`, playlist dialog VMs, `MainWindowViewModel.cs`; `Images.cs`, `Resources.cs`, `IconToImageSourceConverter.cs` | type ambiguity、resource generation、STA / clipboard / notify icon / dialog 依存に加え、WebBrowser の ActiveX / private reflection が MVVM 移行と .NET 10 source compatibility を妨げる | `System.Windows.Forms` と `WebBrowser` を新規 VM / service contract に持ち込まない。P0-03 で player panel / browser を view-host adapter に閉じる | `net10.0-windows` + `UseWindowsForms` 前提で継続するか、WebView2 等へ置換するかを別 ticket で判断する。`System.Drawing.Common` の Windows 限定扱いも確認する |
| D1-07 | External process host | `LR2body.cs`, `uBMplay.cs`, `BMIIDXView2015.cs`, `UpdateDownloadService.cs`, `BeMusicSeeker.Updater/Program.cs`, `ExplorerOpenService.cs`, `App.cs` | `ProcessStartInfo`、working directory、restart / updater、external browser / explorer の挙動が apphost / publish layout と結合している | 新規 launch 処理は service / adapter 経由にする。settings や UI event handler から直接 `Process.Start` を増やさない | player / updater / explorer / browser launch を用途別 gateway に分け、apphost location と command-line quoting を .NET 10 target で再検証する |
| D1-08 | `AppDomain.CurrentDomain.BaseDirectory` / assembly location | `App.cs`, `PortableSettingsPath.cs`, `PortableSettingsProvider.cs`, `SevenZipArchiveExtractor.cs`, `EverythingNative.cs`, `UpdateDownloadService.cs`, `JsonLanguageCatalog.cs`, `BMSLibrary.cs` | base directory が settings path、native DLL、update_work、metadata bundle、language catalog の根になる。publish layout 変更で複数機能が同時に影響を受ける | base directory を root で決め、抽出 service へは path / provider として渡す。`Assembly.GetExecutingAssembly().Location` 直参照を増やさない | app base directory policy を decision record 化する。single-file / self-contained / framework-dependent のうち対応対象を決める |
| D1-09 | Updater project coupling | `BeMusicSeeker.Updater/BeMusicSeeker.Updater.csproj`, `BeMusicSeeker.csproj` の `CopyUpdaterToAppOutput`, `UpdateDownloadService.cs`, `BeMusicSeeker.Updater/Program.cs` | 本体だけ .NET 10 化すると updater の runtime、出力名、copy target、restart path の前提が割れる | release freeze 中は updater 運用を変えない。update / restart 処理は gateway 化候補として残し、app output への copy 前提を inventory に残す | updater を本体と同時に `net10.0-windows` 化するか、外部 `net472` tool として残すかを release 計画で判断する |
| D1-10 | `System.Deployment` reference | `BeMusicSeeker.csproj` の `<Reference Include="System.Deployment" />`。調査時点で source 使用は見当たらない | .NET 10 では不要または非互換になりやすく、参照だけで移行時の警告・失敗要因になる可能性がある | 実使用がないことを別 ticket で確認し、削除候補として扱う。release / ClickOnce 方針とは混ぜない | ClickOnce が必要なら .NET Core 対応方式で再設計する。不要なら本移行前に削除する |

## Dependency inventory

Managed `libs`:

- `Bass.Net.dll`
- `DynamicJson.dll`
- `IniLibrary.dll`
- `Livet.dll`
- `Livet.Extensions.dll`
- `MetroRadiance.Chrome.dll`
- `MetroRadiance.Core.dll`
- `MetroRadiance.dll`
- `Microsoft.Expression.Drawing.dll`
- `Microsoft.Expression.Effects.dll`
- `Microsoft.Expression.Interactions.dll`
- `Microsoft.WindowsAPICodePack.dll`
- `Microsoft.WindowsAPICodePack.Shell.dll`
- `NLog.dll`
- `QuickConverter.dll`
- `SevenZipExtractor.dll`
- `SgmlReaderDll.dll`
- `sqlite.net.dll`
- `System.Collections.Immutable.dll`
- `System.Windows.Interactivity.dll`

Native:

- `native/Everything3_x64.dll`
- `native/EverythingBridge_x64.dll`
- `vendor/native/x64/7z.dll`
- `vendor/native/x64/bass.dll`
- `vendor/native/x64/bass_fx.dll`
- `vendor/native/x64/bassasio.dll`
- `vendor/native/x64/bassenc.dll`
- `vendor/native/x64/bassmix.dll`
- `vendor/native/x64/basswasapi.dll`
- `vendor/native/x64/OggVorbis.NET64.dll`
- `vendor/native/x64/sqlite3.dll`

Custom output targets:

- `RelocateManagedDependenciesToLibs`: `@(ReferenceCopyLocalPaths)` の managed DLL を `$(OutDir)libs` へコピーし、`$(OutDir)` 直下から削除する。
- `RemoveLegacyNativeOutputArtifacts`: `$(OutDir)x86`, `$(OutDir)x64`, `$(OutDir)libs\x86` を削除し、`$(OutDir)libs\x64\OggVorbis.NET64.dll` も削除する。
- `CopyUpdaterToAppOutput`: `BeMusicSeeker.Updater.exe` を app output へコピーする。

## Immediate follow-up candidates

`REF-MVP-D1` 後にすぐ実装 ticket 化しやすいもの:

1. `Settings.Default` boundary の first slice: `BmsLibraryOptionsSnapshot` / playlist URL completion / custom folder output のどれか 1 workflow へ直接参照を閉じる。
2. Native / managed output layout documentation: app / tests の `RelocateManagedDependenciesToLibs`, `7z.dll`, `sqlite3.dll`, Everything bridge, BASS, updater の出力先を 1 つの table にし、後続で csproj cleanup できるようにする。
3. P0-03 follow-up: WinForms dependency を dialog / view-host 側へ閉じ、ViewModel へ新規 `System.Windows.Forms` を増やさない command bridge を作る。
4. P0-03 follow-up: `WindowsFormsHost` / WPF `WebBrowser` / `AxIWebBrowser2` reflection を view-host adapter として棚卸しし、DataContext 移行の blocker にしない。

今は詳細化しないもの:

- `net10.0-windows` TFM 変更。
- managed DLL の置換決定。
- user.config migration 実装。
- native DLL の NuGet / RID layout 化。
- release / publish / updater 運用変更。
