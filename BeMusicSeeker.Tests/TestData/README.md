# TestData 運用メモ

最終更新: 2026-06-15

このディレクトリには、通常の合成データでは確認しづらい互換性や実データ由来の挙動を確認するための fixture を置く。
大容量 fixture は通常テストを重くしやすいため、用途と実行条件を明確にして扱う。

テスト全体の分類と実行方針は [devdocs/spec/testing-strategy.md](../../devdocs/spec/testing-strategy.md) を参照する。

## fixture 一覧

| ディレクトリ | 主な用途 | 通常検証での扱い |
| --- | --- | --- |
| `archives` | 圧縮ファイル処理の小規模 fixture。 | 通常検証で利用可。 |
| `chart_info_real` | BMS 実譜面 1000 件と beatoraja / jbms-parser 参照値の比較。 | 全件検証は `BMS_TEST_CHART_INFO_FULL=1` の opt-in。 |
| `chart_info_bmson_real` | BMSON 実譜面 1034 件と参照値の比較。 | 全件検証は `BMS_TEST_CHART_INFO_FULL=1` の opt-in。 |
| `chart_info_edge_cases` | 巨大 timeline、RANDOM overflow、initial BPM edge case など。 | 小さい edge case は通常検証可。巨大 timeline は opt-in。 |
| `chart_info_production_diff` | production 差分調査用 fixture。 | 全件検証は `BMS_TEST_PRODUCTION_DIFF_FULL=1` の opt-in。 |
| `chart_info_production_latest_diff` | 新しい参照実装差分の少数 fixture。 | 少数検証として通常検証可。 |
| `lr2_builtin_custom_folder_real` | LR2 builtin custom folder の実ファイル互換確認。 | 通常検証で利用可。 |
| `song_snapshot` | 大規模 LR2 `song.db` snapshot。 | 現状は一部通常検証で使用中。新規テストでは小さい合成 DB を優先し、既存テストも段階的に置き換える。 |

## 大容量 fixture の扱い

- `chart_info_real`、`chart_info_bmson_real`、`chart_info_edge_cases` の巨大譜面、`chart_info_production_diff`、`song_snapshot/song.db` は `LargeFixture` として扱う。
- 大容量 fixture を使うテストには、用途に応じて `ParserCompatibilityFull`、`ProductionDiffFull`、`ParserCompatibilitySlow`、`Performance`、`LargeFixture` などの `TestCategory` を付ける。
- 新規または整理済みの大容量 fixture テストでは、通常の `dotnet test` で全件 parse や巨大 DB copy が走らないよう、環境変数 guard を付ける。
- 現状は `song_snapshot/song.db` や一部 `chart_info_*` fixture が `BeMusicSeeker.Tests.csproj` の `CopyToOutputDirectory` 対象に残っている。これは次の整理対象であり、guard だけでは output copy を止められない。
- 新しい regression を追加するときは、まず最小の合成譜面や小さい DB で再現できないか検討する。
- 実データでしか再現できない場合は、全件 fixture に依存する前に、該当 fixture だけを切り出せないか確認する。

## 代表的な opt-in 実行

以下は repo root から実行する。

Parser full 互換:

```powershell
$env:BMS_TEST_CHART_INFO_FULL = "1"
dotnet test BeMusicSeeker-decomp.sln /p:Configuration=Release --filter "TestCategory=ParserCompatibilityFull"
```

Production diff 全件:

```powershell
$env:BMS_TEST_PRODUCTION_DIFF_FULL = "1"
dotnet test BeMusicSeeker-decomp.sln /p:Configuration=Release --filter "TestCategory=ProductionDiffFull"
```

既知 slow fixture:

```powershell
$env:BMS_TEST_CHART_INFO_SLOW = "1"
dotnet test BeMusicSeeker-decomp.sln /p:Configuration=Release --filter "TestCategory=ParserCompatibilitySlow"
```

性能検証:

```powershell
$env:BMS_TEST_PERFORMANCE = "1"
dotnet test BeMusicSeeker-decomp.sln /p:Configuration=Release --filter "TestCategory=Performance"
```

PowerShell の環境変数は同じ session に残る。通常検証へ戻る前に必要なら次のように解除する。

```powershell
Remove-Item Env:BMS_TEST_CHART_INFO_FULL -ErrorAction SilentlyContinue
Remove-Item Env:BMS_TEST_PRODUCTION_DIFF_FULL -ErrorAction SilentlyContinue
Remove-Item Env:BMS_TEST_CHART_INFO_SLOW -ErrorAction SilentlyContinue
Remove-Item Env:BMS_TEST_PERFORMANCE -ErrorAction SilentlyContinue
```
