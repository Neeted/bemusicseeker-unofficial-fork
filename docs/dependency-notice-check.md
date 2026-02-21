# Third-Party Notices Check

## 目的

配布物 (`bin/Release/net472`) に含まれる DLL/フォントが `ThirdPartyNotices.txt` に漏れなく記載されているかを確認します。

## 実行

```powershell
pwsh scripts/deps/check-third-party-notices.ps1
```

または PowerShell:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/deps/check-third-party-notices.ps1
```

## 結果の見方

- `[ERROR] Files present in release but missing from notices` がある場合: 監査未完了です。
- `[WARN] Files listed in notices but not found under release root` は、条件付き配布コンポーネントや古い記載の可能性があります。

## CI組み込み案

- まずは本スクリプトを CI で実行し、終了コード `1` を失敗扱いにする。
- 将来的には `Status: RED` を含む場合も失敗扱いにする拡張を追加する。