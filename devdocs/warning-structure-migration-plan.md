# WARNING 構造化移行計画

## 目的

譜面行の `WARNING` 表示を単純な文字列連結から、種類・優先度・行ハイライト・ダイジェスト表示を持つ構造化 warning へ移行する。

現在は `BMSFile.warning` が主な表示本文で、行の警告色は `HasHighlightedWarning` / `HasLowConfidenceInstallWarning` / `HasZeroNoteMismatchWarning` / `IsHashDuplicated` などの別フラグで制御されている。このため、warning 文字列と行色の寿命がずれやすく、導入先推定 warning のように導入後も色だけ残るケースが起こり得る。

構造化後は、warning の種類を identity として扱い、表示文字列・tooltip・ダイジェスト・行色を同じ warning collection から算出する。

## 前提

- 移行期間中は動作確認目的のみでアプリを起動する。
- 移行途中の状態はリリース品質でなくてよい。
- 最終的には既存の `WARNING` 列、tooltip、行ハイライト、pending install 判定、テストを安定させる。
- `warning` / `DisplayWarning` / `HasHighlightedWarning` は当面互換プロパティとして残す。
- 既存 DB や設定ファイルの互換性を壊さない。
- 既存のリソース文字列はできるだけ再利用する。

## 現状

### 表示経路

- `BMSFile.warning`
  - 旧来の warning 本文。
  - null は getter で空文字扱い。
  - setter で `warning` と `DisplayWarning` の変更通知を出す。
- `BMSFile.DisplayWarning`
  - `warning` に `Warning_ZeroNoteMismatch` を表示時合成する。
- `BMSFile.HasHighlightedWarning`
  - `IsHashDuplicated || HasZeroNoteMismatchWarning || HasLowConfidenceInstallWarning`
  - `warning` が空でなくても、それだけでは行色は付かない。
- `CustomTableColumnFactory`
  - `WARNING` 列は `DisplayWarning` を表示本文、tooltip、sort 対象として使う。
- `CustomTableView`
  - `HasHighlightedWarning` が true の行を警告色で描画する。

### 主な warning 生成元

- リソース不足
  - `BmsLibraryMaintenanceService.ApplyNeedToBeFixedWarnings()`
  - 既存 `warning` を一度 null にしてから WAV/BGA/MOVIE/STAGEFILE/BACKBMP/BANNER warning を作り直す。
- 導入先推定
  - `BMSLibrary.ApplyInstallEstimationResultToFiles()`
  - 文字列は `AppendWarningLine()`、行色は `HasLowConfidenceInstallWarning`。
  - 削除は `RemoveInstallEstimationWarnings()` の翻訳済み文字列 prefix 依存。
- nested chart
  - `BmsLibraryPackageInstallService.ApplyNestedChartFileWarnings()`
  - `PrependWarningLine()` で先頭へ挿入する。
- duplicate
  - `IsHashDuplicated` と duplicate warning 文字列の両方を更新する。
- zero-note mismatch
  - `HasZeroNoteMismatchWarning` のみを持ち、`DisplayWarning` で表示時に文言を足す。

## 課題

- warning の identity が文字列なので、翻訳やテンプレート変更に弱い。
- warning の追加・削除が `Environment.NewLine` と prefix 判定に依存している。
- 行ハイライトの有無が warning 本文と別管理になっている。
- warning の表示順を個別 helper で制御しており、種類が増えるほど破綻しやすい。
- 固定行高化により、一覧セルでは 1 行目しか実質見えないため、1 行目を明示的な digest にしたい。
- リソース不足 warning は詳細としては重要だが、譜面単体や nested 譜面では発生しやすく、常に digest 優先表示するとノイズになる。

## 目標 UI

### 一覧セル

`WARNING` 列の 1 行目は digest 表示にする。

想定形式:

```text
[KindCount] DigestLabel_priority_1st, DigestLabel_priority_2nd
```

例:

```text
[3] サブフォルダ譜面, 推定先複数, リソース不足
```

### Tooltip

tooltip は詳細表示にする。

想定形式:

```text
サブフォルダ譜面:
パッケージ直下以外に有効な譜面ファイルがあります。必要に応じて移動または無効な拡張子へ変更してください。

推定先複数:
D:\BMS\foo
D:\BMS\bar

リソース不足:
WAV: 12 / 20
BGA: 0 / 3
```

### 行ハイライト

行ハイライトは warning 種別の `HighlightRow` から算出する。

ただし、既存の見た目互換を優先するため、初期案では次を維持する。

- duplicate: highlight
- zero-note mismatch: highlight
- 導入先推定低信頼: highlight
- nested chart: no highlight
- resource missing: no highlight
- already installed: no highlight
- single file package: no highlight

## 構造案

### ChartWarning

```csharp
internal sealed class ChartWarning
{
    public ChartWarningKind Kind { get; }
    public ChartWarningCategory Category { get; }
    public int Priority { get; }
    public string DigestLabel { get; }
    public string Message { get; }
    public bool HighlightRow { get; }
    public bool ShowInDigest { get; }
    public bool ShowInTooltip { get; }
    public IReadOnlyDictionary<string, string> Details { get; }
}
```

### ChartWarningKind

```csharp
internal enum ChartWarningKind
{
    LegacyText,
    NestedChartFileInPackage,
    AlreadyInstalled,
    SingleBmsFile,
    SingleBmsonFile,
    ResourceWavMissing,
    ResourceBgaMissing,
    ResourceMovieMissing,
    ResourceStagefileMissing,
    ResourceBackbmpMissing,
    ResourceBannerMissing,
    InstallEstimationAmbiguous,
    InstallEstimationMetadataMismatch,
    InstallEstimationReinstallNotImproved,
    InstalledDestinationResolveFailed,
    DuplicateChart,
    ZeroNoteMismatch
}
```

### ChartWarningCategory

```csharp
internal enum ChartWarningCategory
{
    PackageLayout,
    InstalledState,
    ResourceHealth,
    InstallEstimation,
    Duplicate,
    ChartContent,
    Legacy
}
```

### BMSFile 側

`BMSFile` に warning collection を持たせる。

```csharp
private ChartWarningCollection _warnings;

internal ChartWarningCollection Warnings => _warnings ??= new ChartWarningCollection(this);

public string warning
{
    get => Warnings.LegacyText;
    set => Warnings.SetLegacyText(value);
}

public string DisplayWarning => Warnings.BuildDisplayText(this);

public string WarningDigestText => Warnings.BuildDigestText(this);

public string WarningTooltipText => Warnings.BuildTooltipText(this);

public bool HasHighlightedWarning => Warnings.HasHighlightedWarning;
```

初期移行では `warning` setter は legacy text として扱い、既存コードをすぐには壊さない。段階的に既存コードを `Warnings.Set(...)` / `Warnings.RemoveCategory(...)` へ置き換える。

## 仮 priority / digest label 案

| Kind | Category | Priority | DigestLabel | HighlightRow | Digest 条件 |
| --- | --- | ---: | --- | --- | --- |
| NestedChartFileInPackage | PackageLayout | 10 | サブフォルダ譜面 | false | true |
| ZeroNoteMismatch | ChartContent | 20 | ゼロノート不整合 | true | true |
| DuplicateChart | Duplicate | 30 | 重複譜面 | true | true |
| InstallEstimationAmbiguous | InstallEstimation | 40 | 推定先複数 | true | true |
| InstallEstimationMetadataMismatch | InstallEstimation | 41 | TITLE/ARTIST不一致 | true | true |
| InstallEstimationReinstallNotImproved | InstallEstimation | 42 | 再導入改善なし | true | true |
| InstalledDestinationResolveFailed | InstallEstimation | 43 | 導入先不明 | false | true |
| AlreadyInstalled | InstalledState | 50 | 既に導入済み | false | true |
| SingleBmsFile | PackageLayout | 60 | 単体BMS | false | true |
| SingleBmsonFile | PackageLayout | 60 | 単体BMSON | false | true |
| ResourceWavMissing | ResourceHealth | 80 | リソース不足 | false | `instl_dst` 未設定時のみ |
| ResourceBgaMissing | ResourceHealth | 81 | リソース不足 | false | `instl_dst` 未設定時のみ |
| ResourceMovieMissing | ResourceHealth | 82 | リソース不足 | false | `instl_dst` 未設定時のみ |
| ResourceStagefileMissing | ResourceHealth | 83 | 画像不足 | false | false |
| ResourceBackbmpMissing | ResourceHealth | 84 | 画像不足 | false | false |
| ResourceBannerMissing | ResourceHealth | 85 | 画像不足 | false | false |
| LegacyText | Legacy | 1000 | その他 | false | true |

同一 digest label が複数ある場合、digest では重複表示しない。

例:

```text
[4] サブフォルダ譜面, TITLE/ARTIST不一致, リソース不足
```

この場合の `[4]` は warning object の総数、label は priority 順の distinct label。

## 移行手順

### Phase 1: 互換レイヤ追加

- `ChartWarningKind` / `ChartWarningCategory` / `ChartWarning` / `ChartWarningCollection` を追加する。
- `BMSFile` に `Warnings` を追加する。
- 既存 `warning` は legacy warning として collection へ入れる。
- `DisplayWarning` / `HasHighlightedWarning` は collection から算出する。
- 既存の `HasLowConfidenceInstallWarning` / `HasZeroNoteMismatchWarning` / `IsHashDuplicated` は当面残す。
- UI はまだ `DisplayWarning` を使い続ける。

目的:

- 既存コードの大半を触らずに、構造化 warning の算出経路を作る。

### Phase 2: 表示プロパティ分離

- `BMSFile.WarningDigestText` を追加する。
- `BMSFile.WarningTooltipText` を追加する。
- `LibraryChartRow` / `PlaylistDetailRow` / `PlaylistDetailSourceRow` に透過プロパティを追加する。
- `CustomTableColumnFactory` の `WARNING` 列を次へ変更する。
  - セル本文: `WarningDigestText`
  - tooltip: `WarningTooltipText`
  - sort: 当面 `WarningDigestText`
- 既存 `DisplayWarning` は tooltip 互換用として残す。

目的:

- 固定行高表示に合わせて、一覧と tooltip の責務を分ける。

### Phase 3: 導入先推定 warning を構造化

- `ApplyInstallEstimationResultToFiles()` を `Warnings.Set(...)` へ移行する。
- `RemoveInstallEstimationWarnings()` を `Warnings.RemoveCategory(InstallEstimation)` へ置き換える。
- `HasLowConfidenceInstallWarning` は `Warnings.HasHighlightedWarningByCategory(InstallEstimation)` から算出するか、互換 setter として残す。
- 導入後・導入先確定後に `InstallEstimation` category が必ず消えるようにする。

目的:

- 「導入後も警告色が残る」問題を最初に潰す。

### Phase 4: nested / resource warning を構造化

- `ApplyNestedChartFileWarnings()` を `NestedChartFileInPackage` warning 付与へ変更する。
- `ApplyNeedToBeFixedWarnings()` を `ResourceHealth` category だけ remove + rebuild に変更する。
- resource warning の digest 表示条件を `instl_dst` 未設定時のみへ寄せる。
- stagefile/backbmp/banner は tooltip には出すが、digest には出さない仮仕様にする。

目的:

- priority 制御と nested warning の先頭表示を文字列 prepend から解放する。

### Phase 5: duplicate / zero-note warning を構造化

- duplicate warning を `DuplicateChart` warning と `IsHashDuplicated` 互換フラグに分離する。
- zero-note mismatch を `ZeroNoteMismatch` warning へ移す。
- `DisplayWarning` の zero-note 特別合成を廃止する。

目的:

- 行ハイライト対象も warning object に一本化する。

### Phase 6: legacy 文字列依存の撤去

- `AppendWarningLine()` / `PrependWarningLine()` / `RemoveInstallEstimationWarnings()` を削除または legacy 専用へ隔離する。
- テストを文字列全文比較から、warning kind / digest / tooltip / highlight の検証へ寄せる。
- `warning` setter の利用箇所を必要最小限にする。

目的:

- warning identity を localized text から kind へ完全移行する。

## テスト方針

### Unit

- `ChartWarningCollection`
  - priority 順に digest が作られること。
  - 同一 digest label は 1 回だけ表示されること。
  - `[KindCount]` が warning object 数になること。
  - category remove が対象 warning だけ消すこと。
  - `HighlightRow` が collection 全体に反映されること。
- `BMSFile`
  - `warning` legacy setter が `DisplayWarning` に反映されること。
  - `WarningDigestText` と `WarningTooltipText` が別々に算出されること。
  - warning collection 変更時に `DisplayWarning` / `WarningDigestText` / `WarningTooltipText` / `HasHighlightedWarning` の変更通知が出ること。

### Integration

- pending 復元
  - nested chart warning が priority 先頭に来ること。
  - resource warning は tooltip に残ること。
- 導入先推定
  - ambiguous / metadata mismatch / reinstall not improved が kind として付くこと。
  - 手動導入先確定後に install estimation category が消えること。
  - 導入後に warning 色が残らないこと。
- resource health
  - 再計算時に resource category だけが更新され、nested や install estimation 以外の warning を誤って消さないこと。
- zero-note / duplicate
  - 既存の行ハイライト互換が維持されること。

## 既知リスク

- 既存 `warning` 文字列が DB や pending table に保存されている場合、移行中は legacy text として扱う必要がある。
- `RemoveInstallEstimationWarnings()` は翻訳済み文字列 prefix に依存しているため、移行途中は legacy warning と structured warning が混在する。
- `BmsLibraryMaintenanceService.ApplyNeedToBeFixedWarnings()` は現在 `warning` 全体を消すため、先に構造化 collection を入れないと他 warning を消し続ける。
- `PlaylistDetailRow` は snapshot 行なので、元 `BMSFile` の warning 更新が即時反映されない。必要に応じて view 再構築が必要。
- `CustomTableCellValueCache` は PropertyChanged を契機に row cache を破棄するため、構造化 collection 更新時の通知漏れは表示不整合になる。

## 最初に着手する候補

1. `ChartWarning` 系の型を追加する。
2. `BMSFile` に warning collection と digest/tooltip プロパティを追加する。
3. `CustomTableColumnFactory` の WARNING 列を digest/tooltip 分離へ変更する。
4. 導入先推定 warning だけを構造化へ移す。
5. 導入後の install estimation warning / highlight 残留をテストで固定する。

この順で進めると、最初にユーザー影響の大きい警告色残留を解消しつつ、nested chart warning や resource warning の priority 制御へ進める。
