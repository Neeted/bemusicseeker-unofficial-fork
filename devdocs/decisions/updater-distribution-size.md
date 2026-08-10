# Updater の配布サイズ

`BeMusicSeeker.Updater` は `net10.0-windows` / `win-x64` / self-contained の Native AOT を採用する。

同一 source snapshot (`8942938a`) から一度ずつ publish した Raw EXE は次のとおりだった。

| 設定 | Bytes | Updater publish warning |
| --- | ---: | ---: |
| 従来の CoreCLR single-file | 73,590,553 | 0 |
| full trim + compressed CoreCLR single-file | 11,488,963 | 0 |
| Native AOT (`OptimizationPreference=Size`) | 3,111,424 | 0 |

機能削減用の globalization、stack trace、resource key 設定や warning suppression は加えていない。journal v1 は source-generated `System.Text.Json` metadataを使用し、既存の更新、rollback、recovery testをpublished Native AOT executableで確認する。

Native AOTはそれ自体が単一native executableを生成するため、CoreCLR bundlerの`PublishSingleFile`、compression、native self-extract設定は使用しない。配布packageは従来どおりrootへ`BeMusicSeeker.Updater.exe`一つだけを配置する。
