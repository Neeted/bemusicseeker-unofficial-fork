# 依存関係ポリシー

## 目的

このプロジェクトでは、依存関係の解決を再現可能で監査可能に保ちます。

## ルール

1. 本タスクでは暗黙のアップグレードを行わない
- 同一バージョンかつ同等の識別子を持つ置き換えのみが許可されます。
- 機能的なアップグレードは別のタスクで扱います。

2. 同一性が検証された場合は PackageReference を優先する
- 必須チェック項目: AssemblyName, AssemblyVersion, PublicKeyToken
- 追跡可能性のために FileVersion と SHA256 を記録します。

3. 必要な場合のみ libs\*.dll を保持する
- 信頼できる NuGet の代替手段が存在しない場合は、HintPath による参照を維持します。
- 依存関係インベントリにその理由をドキュメント化します。

4. ネイティブランタイムDLLはリポジトリ内にベンダーリングする
- `vendor/native/x86` と `vendor/native/x64` を使用します。
- ビルド出力には、外部インストールの依存なしでこれらのフォルダが含まれる必要があります。

5. パッケージグラフをロックする
- `packages.lock.json` を使用します。
- ロックファイルを最新の状態に保ち、コミットします。

## 検証

実行:

```powershell
pwsh scripts/deps/inventory.ps1
pwsh scripts/deps/verify.ps1
```
