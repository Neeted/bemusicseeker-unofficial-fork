# .NET 10 Dependency Register

[移行計画](./BeMusicSeeker_NET10移行計画.md) / [blocker台帳](./DOTNET10_MIGRATION_BLOCKERS.md)

調査基準日: 2026-07-26

versionは実行計画を固定するための候補baselineであり、各Outcome開始時に公式package／vendor情報を再確認する。新しいversionへ機械的に追随せず、behavior、license、native ABI、rollback evidenceを優先する。

## Managed dependencies

| ID | Current | Usage / risk | Target decision | Candidate baseline | Owner |
|---|---|---|---|---|---|
| `DEP-CFG-01` | framework `System.Configuration` | generated settingsとcustom provider | configuration adapterを維持しPackageReference化 | `System.Configuration.ConfigurationManager 10.0.10` | `NET10-01` |
| `DEP-LOG-01` | NLog 4.4.3 | logging boundary内で使用 | current supported majorへ更新しconfig／file behavior確認 | NLog 6.1.4 | `NET10-02` |
| `DEP-JSON-01` | Newtonsoft.Json 13.0.3 | persisted／external JSON | patch更新。DynamicJson置換先候補でもある | Newtonsoft.Json 13.0.4 | `NET10-02/04` |
| `DEP-RES-01` | System.Resources.Extensions 8.0.0 | resource serialization | .NET 10系へ更新しresource load smoke | 10.0.10 | `NET10-02` |
| `DEP-TEST-01` | Test SDK 17.12.0, MSTest 3.6.4 | migration verification | current supported versionsへ更新 | Test SDK 18.8.1, MSTest 4.3.2 | `NET10-02` |
| `DEP-AN-01` | Roslynator 4.15.0 | build analyzer | .NET 10で再解決。警告増加を分類し、必要時だけ更新 | current 4.15.0から検証 | `NET10-02` |
| `DEP-UI-01` | `Livet.dll`, `Livet.Extensions.dll` HintPath | ViewModel、command、notification、dispatcherで広範利用 | vertical migration。Extensionsは必要機能だけ置換 | LivetCask 4.0.2候補 | `NET10-03` |
| `DEP-UI-02` | System.Windows.Interactivity／Expression Interactions | legacy XAML behavior | modern behavior packageへ置換 | Microsoft.Xaml.Behaviors.Wpf 1.1.142 | `NET10-03` |
| `DEP-UI-03` | MetroRadiance 3 DLL | Window chrome usageが限定的 | WPF WindowChrome／resourceへ置換し削除 | package更新ではなくremove | `NET10-03` |
| `DEP-UI-04` | Expression Drawing／Effects | XAML drawing／effect | WPF Path／Geometry／Effectへ置換し削除 | remove | `NET10-03` |
| `DEP-OS-01` | Windows API Code Pack 2 DLL | folder pickerが主用途 | WPF OpenFolderDialogへ置換し削除 | framework API | `NET10-03` |
| `DEP-XAML-01` | QuickConverter HintPath | XAMLで広範な式変換 | feature family単位でtyped converter／projectionへ置換 | remove | `NET10-04` |
| `DEP-JSON-02` | DynamicJson HintPath | external JSONのdynamic access | explicit JSON boundaryへ置換。semanticsをgolden test化 | Newtonsoft.Json／System.Text.Json | `NET10-04` |
| `DEP-DOC-01` | SgmlReaderDll HintPath | playlist／HTML parse | maintained packageへ置換しfixture比較 | Microsoft.Xml.SgmlReader 1.8.30 | `NET10-04` |
| `DEP-MISC-01` | IniLibrary HintPath | source usage未確認 | compile／testで不要を確認して削除 | remove | `NET10-04` |
| `DEP-MISC-02` | System.Collections.Immutable HintPath | direct source usage未確認 | framework供給で足りれば削除。必要時だけpackage化 | 10.0.10候補 | `NET10-04` |
| `DEP-DB-01` | sqlite.net HintPath | app／tests／2 toolsのstorage | provider migration＋golden DB test | sqlite-net-pcl 1.11.285 | `NET10-05` |
| `DEP-DB-02` | hand-placed sqlite3.dll | native provider | SQLitePCLRaw bundleへ一元化 | SQLitePCLRaw.bundle_e_sqlite3 3.0.4 | `NET10-05` |
| `DEP-ARC-01` | SevenZipExtractor DLL | archive extraction | PackageReference化しbehavior／native load検証 | SevenZipExtractor 1.0.19 | `NET10-06` |
| `DEP-AUD-01` | OggVorbis.NET64 DLL | Ogg decode | NVorbis parity spike。差異が大きければ明示retain | NVorbis 0.10.5候補 | `NET10-06` |

## Vendor / native dependencies

| ID | Current | Decision | Required evidence | Owner |
|---|---|---|---|---|
| `DEP-AUD-02` | Bass.Net＋BASS native family | vendor-supported x64組合せへ更新または明示retain | exact version、license／redistribution、ABI、device／decode／shutdown smoke | `NET10-06` |
| `DEP-ARC-02` | 7z.dll | SevenZipExtractorと整合するx64 binaryをpublish | version、license、encrypted／failure／path-safety tests | `NET10-06` |
| `DEP-NATIVE-01` | Everything3_x64／EverythingBridge_x64 | x64 bridgeをretain可能 | ABI、installed／absent behavior、publish path、license | `NET10-06` |
| `DEP-UIH-01` | WPF＋WinForms host＋legacy WebBrowser／COM | 初回移行ではretain | startup、host creation、navigation、shutdown、clean-machine smoke | `NET10-03/08` |

## Removal rule

HintPathを消しただけで完了にしない。source usage、XAML、reflection string、resource、test、publish outputを確認し、置換routeと旧binaryの双方を同じunitで閉じる。vendor binaryをretainする場合は、理由、exact version、source、license、architecture、runtime load testをこの台帳へ記録する。
