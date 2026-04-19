# Everything 1.5a `sibling:` + `ext:` 挙動メモ

## 背景
BMSライブラリ初期化の高速化で Everything 1.5a を利用した際、`sibling:<ext:...>` の挙動が想定と異なる可能性を確認した。

## 観測した事象
意図:
- 「拡張子が BMS 系の**ファイル**を持つディレクトリの同階層ファイル」を取得したい。

実際:
- `sibling:<ext:bms;bme;bml;pms>` により、`.bms` などの末尾を持つ**フォルダ名**が条件に含まれるような結果が出るケースがある。
- その結果、本来対象外のディレクトリキーが `FilesByDirectory` に混入し、`everything_verify` で `dirDiff` が発生した。

## 再現に使った例
前提として以下のような「拡張子風サフィックスを持つフォルダ」が存在:
- `D:\BMS\1 EVENT\120915 BOF2012\PeaceCast.bms`  (実体はフォルダ)

クエリ例:
- `folder: <path:"D:\BMS\1 EVENT\120915 BOF2012\"> sibling:<ext:bms;bme;bml;pms>`

観測:
- `PeaceCast.bms` フォルダと同階層の要素がヒットする。

比較クエリ:
- `folder: <path:"D:\BMS\1 EVENT\120915 BOF2012\"> <ext:bms;bme;bml;pms>`

観測:
- 結果 0 件。
- こちらは「`ext:` がファイル拡張子に限定される」期待に近い。

## 解釈（暫定）
- `ext:` 単体の評価と、`sibling:` 内部での評価で対象種別（ファイル/フォルダ）の扱いが一致していない可能性がある。
- 仕様差・実装差・バージョン依存のいずれか（1.5a固有の可能性あり）。

## 影響
- BMSファイル件数（`bmsDiff`）は一致していても、ディレクトリキー（`dirDiff`）だけが増えることがある。
- 初期化ロジックで「BMS含有フォルダのみを想定した辞書」を作る場合、差分要因になる。

## 現在の実装上の対策
- Everything結果に対して `Everything3_IsFolderResult` を用い、フォルダ結果を除外する。
- それでも `dirDiff` が出る場合は、`sibling:` 評価側の候補生成に起因している可能性を疑う。
- `--everything-verify` で Fast 実装との差分ログを継続確認する。

## 今後の扱い案
1. Everything 1.5a の仕様/既知事象として記録し、回避ロジック（後段フィルタ）で吸収する。
2. 必要に応じて最小再現ケースを作り、フォーラム報告を検討する。
3. 検証時は `dirDiff` サンプル（missing/extra）を必ず保存し、回帰確認に使う。
