# chart-info-export

`chart-info-export` は、解析済みの `chart_info` / `chart_digest_map` を既存の `song.db` から取り出し、BeMusicSeeker 起動時に import できる metadata bundle を作成するための開発・リリース用ツールです。

## 目的

`chart_info` の解析は大規模ライブラリでは時間がかかります。リリース物に解析済み metadata を同梱しておくと、ユーザー環境では起動時に差分 import するだけで済み、通常 backfill の parse 対象を減らせます。

このツールは以下を作成できます。

- `chart-info-metadata.db`
- `chart-info-metadata.7z`

アプリは exe と同じ階層にある `chart-info-metadata.db` を優先して import し、なければ `chart-info-metadata.7z` を展開して import します。import 後の bundle は `imported_metadata/` へ退避され、最後に import した同名 cache として保持されます。必要な場合は `imported_metadata/` から exe と同じ階層へ戻すことで再 import できます。

## 前提

- 入力元の `song.db` に `chart_info` table が存在すること。
- `.7z` を作成する場合は 7-Zip CLI (`7z.exe`) が必要です。
- 既定では以下を探索します。
  - `C:\Program Files\7-Zip\7z.exe`
  - `C:\Program Files (x86)\7-Zip\7z.exe`

別の場所にある場合は `--sevenzip` で指定します。

## DB のみ作成

```powershell
dotnet run --project tools/chart-info-export -- `
  --source "D:\LR2beta3\LR2files\Database\song.db" `
  --out artifacts\chart-info-metadata\latest\chart-info-metadata.db
```

## DB と 7z を作成

```powershell
dotnet run --project tools/chart-info-export -- `
  --source "D:\OpenLR2\LR2files\Database\song.db" `
  --out artifacts\chart-info-metadata\latest\chart-info-metadata.db `
  --archive-out artifacts\chart-info-metadata\latest\chart-info-metadata.7z
```

`--archive-out` を指定すると、作成済み DB を一時ディレクトリへ `chart-info-metadata.db` という名前でコピーし、その1ファイルだけを archive root に含めた `.7z` を作成します。

7-Zip の場所を明示する場合:

```powershell
dotnet run --project tools/chart-info-export -- `
  --source "D:\OpenLR2\LR2files\Database\song.db" `
  --out artifacts\chart-info-metadata\latest\chart-info-metadata.db `
  --archive-out artifacts\chart-info-metadata\latest\chart-info-metadata.7z `
  --sevenzip "C:\Program Files\7-Zip\7z.exe"
```

## リリース作成との接続

metadata 同梱版 package は `scripts\publish.ps1` で作成します。

```powershell
.\scripts\publish.ps1 -IncludeMetadata
```

既定では以下の metadata archive を同梱します。

```text
artifacts\chart-info-metadata\latest\chart-info-metadata.7z
```

zip 内ではアプリが認識できるよう、root に `chart-info-metadata.7z` として配置されます。

## よくある失敗

- `Source DB does not contain chart_info table.`
  - 入力元 DB に `chart_info` がありません。解析済み metadata を持つ DB を指定してください。
- `7z.exe was not found.`
  - 7-Zip CLI が見つかりません。7-Zip をインストールするか、`--sevenzip` で `7z.exe` を指定してください。
- `Output DB must be different from source DB.`
  - `--source` と `--out` に同じ path を指定しています。出力先は別ファイルにしてください。
- `Archive output must be different from output DB.`
  - `--out` と `--archive-out` に同じ path を指定しています。
