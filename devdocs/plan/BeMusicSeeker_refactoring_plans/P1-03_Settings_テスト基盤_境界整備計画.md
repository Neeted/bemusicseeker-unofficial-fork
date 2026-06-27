# P1-03 Settings / テスト基盤 / 境界整備計画

[総合計画へ戻る](./BeMusicSeekerリファクタリング計画.md)

## 目的

`Settings.Default` への直接依存と、巨大ファイル前提の source-text tests / private reflection tests は、P0 のリファクタリング全体を妨げる。ここは P0-01 と並行して先に整備する価値が高い。

## 現状観測

| 項目 | 観測 |
|---|---:|
| `Settings.Default` 参照 | `MainWindowViewModel.cs` 約 779 箇所、`BMSPlaylist.cs` 約 49 箇所、`MainWindow.cs` 約 32 箇所、`BMSLibrary.cs` 約 26 箇所 |
| source-text tests | `MainWindowViewModel.cs`, `MainWindow.cs`, `BMSLibrary.cs`, `BMSPlaylist.cs` などを直接 `File.ReadAllText` している |
| private reflection tests | `BMSLibrary`, `SettingDialogViewModel`, `CustomTableView` などの private member に直接依存 |
| risk | partial split / service extract だけで test が落ちる。settings 変更が test 間で漏れやすい |

## 目標アーキテクチャ

```text
Properties.Settings.Default          // persisted compatibility source
└─ AppSettingsStore                  // wrapper / Save / Reload / Reset / migration
   ├─ AppSettingsSnapshot            // immutable-ish read model for services
   ├─ SettingsValidationService
   ├─ SettingsTransaction            // dialog edit session
   └─ TestSettingsScope              // tests only
```

## Ticket SET-0A: SourceTextTestHelper を追加する

変更候補:

- `BeMusicSeeker.Tests/SourceTextTestHelper.cs`

API 候補:

```csharp
internal static class SourceTextTestHelper
{
    public static string FindRepositoryRoot();
    public static string ReadProductionSourceText(params string[] relativePathParts);
    public static string ReadFiles(params string[] absolutePaths);
    public static string ReadMainWindowViewModelSourceText();
    public static string ReadMainWindowSourceText();
    public static string ReadBmsLibrarySourceText();
    public static string ReadBmsPlaylistSourceText();
}
```

作業:

1. deterministic order でファイルを連結する。
2. `\r\n` / `\n` は test ごとに必要なら normalize する。
3. P0-01 Ticket A で先行する場合は、その helper を再利用する。
4. 直接 `File.ReadAllText(... MainWindowViewModel.cs)` / `File.ReadAllText(... MainWindow.cs)` / `File.ReadAllText(... BMSLibrary.cs)` / `File.ReadAllText(... BMSPlaylist.cs)` している test を置き換える。
5. 着手時は次で棚卸しする。
   ```powershell
   rg "File\.ReadAllText.*MainWindowViewModel\.cs|File\.ReadAllText.*MainWindow\.cs|File\.ReadAllText.*BMSLibrary\.cs|File\.ReadAllText.*BMSPlaylist\.cs" .\BeMusicSeeker.Tests -g "*.cs"
   ```

受け入れ条件:

- partial split で source-text test が壊れない。
- 既存 test の設計意図は維持される。
- P0-01 / P0-02 / P1-01 の巨大ファイル分割に同じ helper を使い回せる。
- ViewModel / View / BMSLibrary / BMSPlaylist の直読み検査が、それぞれ対応する helper API 経由になっている。

## Ticket SET-0B: ReflectionTestInventory を作る

作業:

1. `BindingFlags.NonPublic`, `GetField`, `GetMethod` を grep する。
2. `.tmp/refactor/private-reflection-test-inventory.md` に target type / member / test / replacement plan を記録する。ticket をまたいで参照する必要が出たら `devdocs/` 側へ移す。
3. replacement plan は次に分類する。
   - service 抽出後に internal API test へ移す。
   - public compatibility API として残す。
   - source-text / behavior test へ置換する。
   - 当面維持する。

受け入れ条件:

- 各 P0/P1 ticket で private member 移動時の test 方針が分かる。

## Ticket SET-1A: `AppSettingsSnapshot` を作る

新規候補:

```text
BeMusicSeeker/Properties/AppSettingsSnapshot.cs
BeMusicSeeker/Properties/AppSettingsStore.cs
```

初期 scope:

- 新規抽出 service が必要とする値だけ入れる。
- 全 settings を一度に移さない。

作業:

1. `Settings.Default` から immutable-ish snapshot を生成する。
2. snapshot は null / whitespace / path normalization の方針を明示する。
3. `BmsLibraryOptionsSnapshot` と重複する設定は、すぐ統合せず橋渡しを作る。

受け入れ条件:

- 新規 service は `Settings.Default` でなく snapshot を受け取れる。
- persisted setting name は変えない。

## Ticket SET-1B: settings edit transaction を SettingDialog へ導入する

関連:

- P0-01 の `SettingDialogViewModel` 分割

作業:

1. `SettingDialogViewModel` が多数の settings 値を一時保持している現状を、`SettingsEditSession` / `SettingsTransaction` に寄せる。
2. validation は `SettingsValidationService` へ移す。
3. save 後処理は `SettingsPostSaveCoordinator` へ移す。

受け入れ条件:

- dialog VM は UI edit state と command に集中する。
- settings validation / post-save side effect を単体 test できる。

## Ticket SET-1C: `TestSettingsScope` を追加する

新規候補:

- `BeMusicSeeker.Tests/TestSettingsScope.cs`

作業:

1. test 開始時に対象 settings の snapshot を保持する。
2. Dispose 時に元へ戻す。
3. `Settings.Default.Save()` が必要な test と不要な test を分類する。
4. settings を触る test へ適用する。

受け入れ条件:

- settings 由来の test 間干渉が減る。
- 必要なら `[DoNotParallelize]` を明示する。

## Ticket SET-2A: 新規 service で `Settings.Default` 直接参照を禁止する設計 test を追加する

作業:

1. `ViewModels/MainWindow/**/*.cs` や新規 `Models/BmsLibraryInternal/*Coordinator.cs` を対象にする。
2. allowlist を作る。
   - `AppSettingsStore`
   - `BmsLibraryOptionsSnapshot`
   - generated `Settings.cs`
   - legacy facade transition files
3. source-text test で `Settings.Default` direct reference を検出する。

受け入れ条件:

- 新規抽出 service が settings global に戻らない。
- 既存 legacy file は allowlist に理由付きで残す。

## Ticket SET-2B: dialog route / MessageBox route tests を分割後構造に合わせる

関連:

- `DialogRouteConsolidationTests.cs`
- `MainWindowContextMenuResourceTests.cs`

作業:

1. file path 固定の test を helper ベースにする。
2. 禁止 pattern は production 全体または領域別 glob で検査する。
3. `UiDialogCoordinator` 経由であることを test する。

受け入れ条件:

- View / ViewModel / Model の dialog 依存境界が test で守られる。
- file split が test を不安定にしない。

## 完了目標

- P0-01〜03 の分割が test 構造に阻まれない。
- settings global 依存が新規 service に広がらない。
- `.NET 10` 移行時に `System.Configuration` / `Settings.Default` を置き換える入口ができる。
