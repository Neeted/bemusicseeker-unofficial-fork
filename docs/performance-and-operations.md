# 性能・運用メモ

## 1. 初期化スキャン経路

`BMSLibrary._initialize(songTblFileCheck=true)` で実施。

優先順:

1. `EverythingFileScanner`（Bridge必須）
2. 失敗時 `FastDirectoryFileScanner`（ローカル列挙）

## 2. 現行Everything経路の前提

- Managed Everything（`Everything3_*` を1件ずつ読む経路）は削除済み。
- Bridge経路のみ使用:
  - `native/Everything3_x64.dll`
  - `native/EverythingBridge_x64.dll`
- Bridge失敗時は Fast へフォールバック。

## 3. 起動引数

- `--log-level=Info|Warn|Error`  
  通常ログの最小レベルを指定（既定: `Warn`）
  `Info` の場合は性能ログも有効化
- `--everything-verify`  
  Everything結果と Fast結果を比較検証（重い）

出力先:

- `application.log`（通常ログ）
- `install-performance.log`（`--log-level=Info` 時の `InstallPerformance*`）
- `everything-verify.log`（`--everything-verify` 時の verify 専用）

## 4. verifyモードの注意

`--everything-verify` は意図的に重い。

- Everythingスキャン後に Fastスキャンを追加実行
- ハッシュベースで差分比較
- `everything_verify comparedMs=...` が長いのは仕様

通常運用では `--everything-verify` を外すこと。

## 5. 導入処理の主な最適化済み点

- 推定先インストールの選択全体バッチ化
- pending/installed の末尾一括反映
- 導入中UI更新抑制（ViewModel側）
- ヘルスチェックのバッチ末尾集約（必要箇所）
- 0ノート判定の対象限定化

## 6. ログ観測の目安

- `everything_scan totalMs`
  - スキャン全体時間
- `nativeBridgeUsed`
  - Bridge利用成否
- `nativeBridgeReason`
  - Bridge失敗理由（DLL不足など）
- `bms_scan totalMs`
  - スキャン適用（verify含む）最終時間

`everything_verify` が有効な場合、`bms_scan totalMs` は verify時間を含む点に注意。

## 7. 運用時のDLL配置

ランタイムのソースオブトゥルース:

- `native/Everything3_x64.dll`
- `native/EverythingBridge_x64.dll`

`bin/.../native/` はビルド出力。手編集しない。
