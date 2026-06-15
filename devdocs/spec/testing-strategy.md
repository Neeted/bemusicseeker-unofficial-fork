# テスト運用方針

最終更新: 2026-06-15

この文書は、BeMusicSeeker のテストを「何を確認するためのものか」で分類し、通常検証を重くしすぎず、必要な互換検証や性能検証を見落とさないための運用方針をまとめる。

## 基本方針

- 通常の `dotnet test` は、リリースに致命的な欠陥がないことを確認する機能検証として扱う。
- 実譜面全件、巨大 DB、大容量 fixture、性能測定は、標準確認手順の通常テストへ無条件に入れない。
- parser や sort engine のように参照実装互換が重要な領域では、通常テスト用の軽量 smoke と、明示実行する full 検証を分ける。
- 性能検証は、機能検証の pass/fail ではなく、変更前後の比較や退行調査のための明示的な測定として扱う。
- 大容量 fixture を使うテストは、テスト名、`TestCategory`、環境変数 guard のいずれかから opt-in であることが分かるようにする。
- 既存テストには巨大 DB を使う通常検証がまだ残っている。これは当面の例外であり、新規追加時の標準にはしない。

## テスト分類

### 通常検証

目的:

- リリース前に、主要機能、DB schema、設定、ViewModel、起動処理、playlist、軽量 parser smoke に重大な回帰がないことを確認する。

実行:

```powershell
dotnet test BeMusicSeeker-decomp.sln /p:Configuration=Release
```

通常検証に入れてよいもの:

- 合成データや小さい fixture で完結する機能検証。
- 代表的な parser regression を小さい文字列や少数 fixture で確認するテスト。
- 実 DB を使う場合でも、必要最小限の小さい DB で済むテスト。

通常検証に入れないもの:

- 実譜面を数百から数千件 parse する全件互換検証。
- `song_snapshot/song.db` のような巨大 DB を大量にコピーする検証。
- 処理時間や比率の比較を目的にした性能測定。
- 失敗時の意味が「機能不具合」ではなく「互換精度の棚卸し」や「性能傾向の変化」になる検証。

現状では `song_snapshot/song.db` を使う playlist / schema 系の通常テストが残っている。これらは機能検証としての意味があるため単純に skip せず、小さい合成 DB へ置き換える対象として扱う。

### Parser 互換検証

目的:

- `ChartInfoParser`、BMS/BMSON decode、`chart_info` 生成を変更したときに、beatoraja / jbms-parser 互換を高精度に確認する。

通常検証:

- 小さい合成譜面、少数の実 fixture、既知の edge case を使う。
- `TestCategory("Compatibility")` は通常検証にも含まれる。重いとは限らない。

Full 検証:

```powershell
$env:BMS_TEST_CHART_INFO_FULL = "1"
dotnet test BeMusicSeeker-decomp.sln /p:Configuration=Release --filter "TestCategory=ParserCompatibilityFull"
```

Full 検証の対象例:

- `chart_info_real` の BMS 実譜面 1000 件比較。
- `chart_info_bmson_real` の BMSON 実譜面 1034 件比較。
- `chart_info_edge_cases` の巨大 timeline reference fixture。

Production diff 検証:

```powershell
$env:BMS_TEST_PRODUCTION_DIFF_FULL = "1"
dotnet test BeMusicSeeker-decomp.sln /p:Configuration=Release --filter "TestCategory=ProductionDiffFull"
```

既知 timeout / slow fixture:

```powershell
$env:BMS_TEST_CHART_INFO_SLOW = "1"
dotnet test BeMusicSeeker-decomp.sln /p:Configuration=Release --filter "FullyQualifiedName~ChartInfoMetadataTests.ParseProductionDiffFixture_KnownTimeoutRows_PerformanceAndExpectedValues"
```

Parser を変更した場合の推奨:

- まず通常検証を実行する。
- `ChartInfoParser`、`ChartInfoBuildService`、BMSON/BMS decode、`chart_info` schema 投影に触れた場合は、`BMS_TEST_CHART_INFO_FULL=1` の full 検証を追加する。
- 参照 DB 差分や production data 由来の修正では、必要に応じて production diff 検証も追加する。

### 大容量 DB / 実データ互換検証

目的:

- LR2 `song.db`、playlist、custom folder、sort などについて、実運用に近い大きなデータで互換性や処理経路を確認する。

扱い:

- `TestCategory("LargeFixture")` を付ける。
- 通常検証では小さい合成 DB を優先する。
- `song_snapshot/song.db` のような巨大 DB は、互換性確認や性能調査のための opt-in fixture として扱う。
- 既存の `song_snapshot/song.db` 使用テストは段階的な整理対象であり、機能検証を失わないよう小さい DB 化してから通常実行から外す。

注意:

- 巨大 DB を temp へコピーするテストは、クラス単位の実行時間だけでなくディスク I/O と test output の肥大化にも影響する。
- 単純な callback、schema、失敗時の後始末を確認するテストでは、巨大 DB ではなく最小 DB を作る。
- 実 DB 互換そのものを確認するテストだけが巨大 DB を使う。

### 性能検証

目的:

- 最適化の効果確認、退行調査、実装方式の比較を行う。

実行:

```powershell
$env:BMS_TEST_PERFORMANCE = "1"
dotnet test BeMusicSeeker-decomp.sln /p:Configuration=Release --filter "TestCategory=Performance"
```

扱い:

- 通常検証には含めない。
- 環境差で揺れるため、原則として厳密な時間閾値で通常テストを失敗させない。
- 測定結果は `TestContext.WriteLine`、`Trace.WriteLine`、または専用ログへ出し、変更前後の比較に使う。

## カテゴリと環境変数

| 種別 | `TestCategory` | 通常実行 | opt-in 条件 |
| --- | --- | --- | --- |
| 機能検証 | 省略または機能名 | 実行する | なし |
| 軽量互換検証 | `Compatibility` | 実行する | なし |
| Parser full 互換 | `ParserCompatibilityFull` | skip / inconclusive | `BMS_TEST_CHART_INFO_FULL=1` |
| Production diff full | `ProductionDiffFull` | skip / inconclusive | `BMS_TEST_PRODUCTION_DIFF_FULL=1` |
| Parser slow 互換 | `ParserCompatibilitySlow` | skip / inconclusive | `BMS_TEST_CHART_INFO_SLOW=1` |
| 大容量 fixture | `LargeFixture` | 原則 skip / inconclusive | テストごとの環境変数 |
| 性能検証 | `Performance` | skip / inconclusive | `BMS_TEST_PERFORMANCE=1` |

`Compatibility` は「互換性を検証する」という意味であり、重いとは限らない。重い互換検証には `ParserCompatibilityFull`、`ProductionDiffFull`、`ParserCompatibilitySlow`、`LargeFixture` を追加する。

## 新しいテストを追加するときの判断基準

- 通常検証に入れるなら、少数の合成データで同じ不具合を再現できないかを先に考える。
- 実データでしか再現できない場合でも、最小の fixture に切り出せるなら全件 fixture へ依存しない。
- parser の参照実装差分を固定するテストは、最小譜面を作れるなら通常検証へ置く。
- 実譜面全体の差分数や互換率を確認するテストは full 互換検証へ置く。
- 性能を確認するテストは、機能テストのついでに時間を測るのではなく、`Performance` と環境変数 guard を付ける。

## 現状の主な大容量 fixture

詳細は [BeMusicSeeker.Tests/TestData/README.md](../../BeMusicSeeker.Tests/TestData/README.md) を参照する。

- `song_snapshot/song.db`: 大規模 LR2 song DB snapshot。
- `chart_info_real`: BMS 実譜面互換 sample。
- `chart_info_bmson_real`: BMSON 実譜面互換 sample。
- `chart_info_edge_cases`: 巨大 timeline などの edge case。
- `chart_info_production_diff`: production 差分調査用 fixture。

## 整理の優先順位

1. `Performance`、`ParserCompatibilityFull`、`ProductionDiffFull`、`ParserCompatibilitySlow` などの重い検証を通常テストから外す。
2. `song_snapshot/song.db` を使う playlist / DB 系テストを、小さい合成 DB に置き換えられるものから置き換える。
3. `BeMusicSeeker.Tests.csproj` の大容量 fixture コピーを見直し、通常 build/test の output を肥大化させない。
4. parser 変更時に実行すべき full 検証コマンドを PR / release 確認手順へ明記する。
