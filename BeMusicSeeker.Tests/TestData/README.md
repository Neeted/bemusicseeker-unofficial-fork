# TestData 運用メモ

最終更新: 2026-08-14

このディレクトリには、通常の合成データでは確認しづらい互換性や実データ由来の挙動を確認するための fixture を置く。
大容量 fixture は通常テストを重くしやすいため、用途と実行条件を明確にして扱う。

テスト全体の分類と実行方針は [devdocs/spec/testing-strategy.md](../../devdocs/spec/testing-strategy.md) を参照する。

## fixture 一覧

| ディレクトリ | 主な用途 | 通常検証での扱い |
| --- | --- | --- |
| `archives` | 圧縮ファイル処理の小規模 fixture。 | 通常検証で利用可。 |
| `chart_info_real` | BMS 実譜面 1000 件と beatoraja / jbms-parser 参照値の比較。 | 全件検証は `BMS_TEST_CHART_INFO_FULL=1` の opt-in。 |
| `chart_info_bmson_real` | BMSON 実譜面 1034 件と参照値の比較。 | 全件検証は `BMS_TEST_CHART_INFO_FULL=1` の opt-in。 |
| `chart_info_edge_cases` | 巨大 timeline、RANDOM overflow、initial BPM edge case などの実譜面参照値。 | `BMS_TEST_CHART_INFO_FULL=1` の opt-in。小さい合成 edge case は通常検証に残す。 |
| `chart_info_production_diff` | production 差分調査用 fixture。 | 全件検証は `BMS_TEST_PRODUCTION_DIFF_FULL=1` の opt-in。 |
| `chart_info_production_latest_diff` | 新しい参照実装差分の少数だが大容量な fixture。 | `BMS_TEST_PRODUCTION_DIFF_FULL=1` の opt-in。 |
| `lr2_builtin_custom_folder_real` | LR2 builtin custom folder の実ファイル互換確認。 | 通常検証で利用可。 |

## 外部 audio encoder smoke

`ExternalAudioEncoderSmokeTests` は `lame.exe`、`neroAacEnc.exe`、`opusenc.exe`、`flac.exe`、`oggenc2.exe` を fixture として保存しない。`BMS_TEST_AUDIO_ENCODERS=1` の明示 opt-in 時だけ、`BMS_TEST_AUDIO_ENCODER_DIR` または production の検索先にある利用者提供 binary を使う。入力 WAV はテスト実行中に deterministic に生成し、終了時に一時ファイルと encoder owner を cleanup する。

```powershell
$env:BMS_TEST_AUDIO_ENCODERS = "1"
$env:BMS_TEST_AUDIO_ENCODER_DIR = "<folder containing available encoder executables>"
# 必要なら required subset を指定する
$env:BMS_TEST_AUDIO_ENCODER_TYPES = "MP3_LAME,OPUS,FLAC,OGG_VORBIS"
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Quick -TestFilter 'FullyQualifiedName~ExternalAudioEncoderSmokeTests'
```

指定した binary が無い場合、または subset 未指定で一つも見つからない場合は test を skip せず fail する。tool が無い環境での実行結果は、real encoder pass 未実施の deterministic execution record として扱う。

## 大容量 fixture の扱い

- `chart_info_real`、`chart_info_bmson_real`、`chart_info_edge_cases` の実譜面、`chart_info_production_diff`、`chart_info_production_latest_diff` は `LargeFixture` として扱う。
- 大容量 fixture を使うテストには、用途に応じて `ParserCompatibilityFull`、`ProductionDiffFull`、`ParserCompatibilitySlow`、`LargeFixture` などの `TestCategory` を付ける。
- 新規または整理済みの大容量 fixture テストでは、通常の `dotnet test` で全件 parse や巨大 DB copy が走らないよう、環境変数 guard を付ける。
- `chart_info_real` と `chart_info_edge_cases` は、build 開始前に `BMS_TEST_CHART_INFO_FULL=1` が設定されている場合だけ test output へコピーする。通常 build はこれら約 137 MiB をコピーしない。
- opt-in flag が無い場合は対象テストを `Inconclusive` とする。flag があるのに必要な fixture が無い場合は明示的に失敗させ、検証済みとして扱わない。
- 新しい regression を追加するときは、まず最小の合成譜面や小さい DB で再現できないか検討する。
- 実データでしか再現できない場合は、全件 fixture に依存する前に、該当 fixture だけを切り出せないか確認する。

## 代表的な opt-in 実行

以下は repo root から実行する。

Parser full 互換:

```powershell
$env:BMS_TEST_CHART_INFO_FULL = "1"
dotnet test BeMusicSeeker.sln /p:Configuration=Release --filter "TestCategory=ParserCompatibilityFull"
```

Production diff（全件および latest diff）:

```powershell
$env:BMS_TEST_PRODUCTION_DIFF_FULL = "1"
dotnet test BeMusicSeeker.sln /p:Configuration=Release --filter "TestCategory=ProductionDiffFull"
```

既知 slow fixture:

```powershell
$env:BMS_TEST_CHART_INFO_SLOW = "1"
dotnet test BeMusicSeeker.sln /p:Configuration=Release --filter "TestCategory=ParserCompatibilitySlow"
```

PowerShell の環境変数は同じ session に残る。通常検証へ戻る前に必要なら次のように解除する。

```powershell
Remove-Item Env:BMS_TEST_CHART_INFO_FULL -ErrorAction SilentlyContinue
Remove-Item Env:BMS_TEST_PRODUCTION_DIFF_FULL -ErrorAction SilentlyContinue
Remove-Item Env:BMS_TEST_CHART_INFO_SLOW -ErrorAction SilentlyContinue
```
