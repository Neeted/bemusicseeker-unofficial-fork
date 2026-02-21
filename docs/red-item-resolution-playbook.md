# Red Item Resolution Playbook

最終更新: 2026-02-21  
目的: Red判定の依存について、出典特定とライセンス確定を再現可能な手順に落とし込む。

この文書は技術監査手順です。法的助言ではありません。

## 0. 共通ルール (全Red項目)

1. まず現物バイナリの指紋を確定する。  
2. 候補配布元から同名/同バージョンバイナリを取得する。  
3. SHA256一致を確認する。  
4. 一致した配布元の一次ライセンス文書を取得する。  
5. `ThirdPartyNotices.txt` と `third_party/licenses/` を更新する。  

共通コマンド:

```powershell
$targets = @(
  "libs/SevenZipExtractor.dll",
  "vendor/native/x86/OggVorbis.NET.dll",
  "vendor/native/x64/OggVorbis.NET64.dll",
  "libs/IniLibrary.dll",
  "resources/commodore-20rounded-20v1.2.ttf"
)
$targets | ForEach-Object {
  if (Test-Path $_) {
    $h = (Get-FileHash $_ -Algorithm SHA256).Hash
    "{0}`t{1}" -f $_, $h
  }
}
```

---

## 1. SevenZipExtractor.dll

対象: `libs/SevenZipExtractor.dll`  
現状SHA256: `E5C7F62D36A298FB80ACA242D744F60AC10D0FA00CDC4143A8FF90A1CDD6AD9B`

### URL候補

- 公式候補A: `https://www.nuget.org/packages/SevenZipExtractor`
- 公式候補B: `https://github.com/adoconnection/SevenZipExtractor`
- 補助: `https://www.nuget.org/api/v2/package/SevenZipExtractor/1.0.19`

### 検証コマンド

```powershell
$tmp = ".tmp/red-check/sevenzipextractor"
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

# NuGet package download
$nupkg = Join-Path $tmp "SevenZipExtractor.1.0.19.nupkg"
Invoke-WebRequest "https://www.nuget.org/api/v2/package/SevenZipExtractor/1.0.19" -OutFile $nupkg

# Extract and hash compare (net45 expected for this project)
$zip = Join-Path $tmp "SevenZipExtractor.1.0.19.zip"
Copy-Item $nupkg $zip -Force
Expand-Archive $zip -DestinationPath (Join-Path $tmp "pkg") -Force

$repoHash = (Get-FileHash "libs/SevenZipExtractor.dll" -Algorithm SHA256).Hash
$pkgHash = (Get-FileHash (Join-Path $tmp "pkg/lib/net45/SevenZipExtractor.dll") -Algorithm SHA256).Hash
"repo=$repoHash"
"pkg =$pkgHash"

# nuspec license metadata
[xml]$nuspec = Get-Content (Join-Path $tmp "pkg/SevenZipExtractor.nuspec")
$nuspec.package.metadata.license
$nuspec.package.metadata.licenseUrl
$nuspec.package.metadata.projectUrl
```

### 完了条件

- `lib/net45/SevenZipExtractor.dll` のSHA256が一致する。
- 対応する upstream (`nuget` と `github`) を一次情報として記録できる。
- ライセンス本文 (例: MIT) を `third_party/licenses/` に同梱し、`ThirdPartyNotices.txt` を Red -> Green/Yellow に更新。

---

## 2. OggVorbis.NET.dll / OggVorbis.NET64.dll

対象:  
- `vendor/native/x86/OggVorbis.NET.dll`  
- `vendor/native/x64/OggVorbis.NET64.dll`  

現状SHA256:  
- x86: `A90783D77CFF7CB80D2A275547E7825E09A6DA639302DCA51BFD1973493729E6`  
- x64: `CB860F8C8B2837C0D5791CDE1A0654CD35280F92A231F47DB0E9F51286CDD9AF`

### URL候補

- 候補A (コード探索): `https://github.com/search?q=OggVorbisDotNet64&type=code`
- 候補B (識別子探索): `https://github.com/search?q=%22Ogg+Vorbis+Decoder+DLL+for+.NET%22&type=repositories`
- 候補C (公開配布探索): `https://www.nuget.org/packages?q=OggVorbis.NET`

### 検証コマンド

```powershell
# 既存メタ情報確認
$files = @(
  "vendor/native/x86/OggVorbis.NET.dll",
  "vendor/native/x64/OggVorbis.NET64.dll"
)
$files | ForEach-Object {
  $h = (Get-FileHash $_ -Algorithm SHA256).Hash
  $vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path $_))
  "=== $_ ==="
  "sha256=$h"
  "product=$($vi.ProductName)"
  "copyright=$($vi.LegalCopyright)"
}

# 64bit側のマネージド属性（クラス名）確認
$asm = [Reflection.Assembly]::LoadFrom((Resolve-Path "vendor/native/x64/OggVorbis.NET64.dll"))
$asm.GetTypes() | Select-Object -First 20 -ExpandProperty FullName
```

### 完了条件

- upstream配布元（リポジトリ or パッケージ）が特定される。
- upstream配布物とSHA256一致が確認できる。
- ライセンス本文と再配布条件（商用可否・表示義務）を一次情報で確認できる。
- 一次情報が取れない場合は「同梱停止」または「代替ライブラリ置換」の方針を明記して Red を解消。

---

## 3. IniLibrary.dll

対象: `libs/IniLibrary.dll`  
現状SHA256: `3C17A60A5F70F49E6074DEFD019B2F3DA72808D218430C42DD223BE2616524F0`

### URL候補

- 候補A (NuGet探索): `https://www.nuget.org/packages?q=IniLibrary`
- 候補B (GitHub探索): `https://github.com/search?q=IniLibrary.dll+1.0.0.0&type=code`
- 候補C (一般探索): `https://www.google.com/search?q=%223C17A60A5F70F49E6074DEFD019B2F3DA72808D218430C42DD223BE2616524F0%22`

### 検証コマンド

```powershell
$file = "libs/IniLibrary.dll"
$h = (Get-FileHash $file -Algorithm SHA256).Hash
$vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path $file))
"sha256=$h"
"fileVersion=$($vi.FileVersion)"
"product=$($vi.ProductName)"
"copyright=$($vi.LegalCopyright)"

# 参照される公開型を抽出（同名候補ライブラリの照合に使う）
$asm = [Reflection.Assembly]::LoadFrom((Resolve-Path $file))
$asm.GetExportedTypes() | Select-Object -ExpandProperty FullName
```

### 完了条件

- 実体と一致する upstream 配布元（ソース or パッケージ）を特定。
- upstreamライセンス本文を取得・同梱。
- `ThirdPartyNotices.txt` の `IniLibrary` を Unknown から確定ライセンスへ更新。
- 特定不能なら Red維持のまま「置換 or 削除期限」を設定する。

---

## 4. commodore-20rounded-20v1.2.ttf

対象: `resources/commodore-20rounded-20v1.2.ttf`  
現状SHA256: `D4070C0FC04BB1F53C38389027CBA56BCAEDF7BFA063D3B6D8EE533A77E99DD7`

### URL候補

- 候補A: `https://www.dafont.com/commodore-64.font`
- 候補B: `http://www.devincook.com/` (作者サイト、到達可否要確認)
- 候補C (補助): `https://fontmeme.com/commodore-64-font/`

### 検証コマンド

```powershell
$font = "resources/commodore-20rounded-20v1.2.ttf"
(Get-FileHash $font -Algorithm SHA256).Hash

# フォント内部名称/著作権の確認
Add-Type -AssemblyName PresentationCore
$glyph = New-Object System.Windows.Media.GlyphTypeface((Resolve-Path $font))
$glyph.FamilyNames
$glyph.Copyrights
$glyph.VersionStrings
```

### 完了条件

- 配布元ページで、商用利用・再配布の明示的許諾を確認できる。
- 可能なら配布アーカイブ内 `README` / `license` の一次文書を保存・同梱。
- 許諾が「使用のみ」で再配布が不明なら Red維持（同梱不可扱い）として方針を決める。

---

## 5. 証跡の保存先（推奨）

監査証跡は次に保存する:

- `docs/evidence/red/sevenzipextractor/`
- `docs/evidence/red/oggvorbis-dotnet/`
- `docs/evidence/red/inilibrary/`
- `docs/evidence/red/commodore-font/`

最低限保存するもの:

1. 取得元URL一覧 (`sources.txt`)
2. ハッシュ比較結果 (`hash-compare.txt`)
3. ライセンス本文または条項抜粋 (`license.txt`)
4. 判定メモ (`decision.md`)

---

## 6. 実行結果 (2026-02-21) と判定更新案

### 6-1. `SevenZipExtractor.dll`

実行結果:

- 現物SHA256: `E5C7F62D36A298FB80ACA242D744F60AC10D0FA00CDC4143A8FF90A1CDD6AD9B`
- NuGet `SevenZipExtractor` 全20バージョン照合の結果、`1.0.12` の `lib/net45/SevenZipExtractor.dll` がSHA一致。
- `1.0.12` の `projectUrl`: `https://github.com/adoconnection/SevenZipExtractor`
- 上記リポジトリの `LICENSE` 取得可 (`MIT License`)。

判定更新案:

- **Red -> Green (可)**  
理由: 現物バイナリと公開配布物のSHA一致、およびMITライセンス本文を一次情報で確認できたため。

残作業:

1. `ThirdPartyNotices.txt` の `SevenZipExtractor` を Green へ更新。
2. `third_party/licenses/MIT.txt` の参照を該当項目に明記。
3. 根拠として `version=1.0.12` を記録。

### 6-2. `OggVorbis.NET.dll` / `OggVorbis.NET64.dll`

実行結果:

- 現物SHA256:
  - x86: `A90783D77CFF7CB80D2A275547E7825E09A6DA639302DCA51BFD1973493729E6`
  - x64: `CB860F8C8B2837C0D5791CDE1A0654CD35280F92A231F47DB0E9F51286CDD9AF`
- x64アセンブリ属性から以下を確認:
  - Product: `Ogg Vorbis Decoder DLL for .NET`
  - Copyright:
    `Copyright (C) 2008-2011 tu-sa, Copyright (C) 2002-2008 Xiph.org Foundation`
- 配布元候補として `https://github.com/ttsuki/OggVorbis.NET` を確認。
- ローカルclone `D:\github-clone\OggVorbis.NET` にて以下を確認:
  - `COPYING.txt` に wrapper/libogg/libvorbis のBSD-3-Clause系条項が記載
  - `AssemblyInfo.cpp` の `AssemblyTitle/AssemblyProduct/AssemblyCopyright`
    が現物バイナリの表記と整合
- ただし `vendor/native` のDLLと clone `bin` 産物のSHA256は不一致（厳密同一ビルドは未確認）。

判定更新案:

- **Red -> Yellow (可)**  
理由: upstreamとライセンス本文は確定できたが、厳密SHA一致/ビルド出典は未確定のため。

Green化条件:

1. 現在同梱中DLLに対応するビルド手順（コミット/設定）を確定。
2. 可能なら再ビルドで同一または実質同等性（公開型・依存・挙動）の証跡を保存。
3. `docs/evidence/red/oggvorbis-dotnet/` に証跡を集約。

### 6-3. `IniLibrary.dll`

実行結果:

- 現物SHA256: `3C17A60A5F70F49E6074DEFD019B2F3DA72808D218430C42DD223BE2616524F0`
- メタ情報:
  - FileVersion: `1.0.0.0`
  - Product: `IniLibrary`
  - Copyright:
    `Copyright ©  2009`
- 公開型:
  - `System.Ini.IniDocument`, `System.Ini.IniReader`, `System.Ini.IniWriter` など
- 原著者ビルド由来の状況証拠を確認:
  - 原本インストール:
    `C:\Users\kazuk\AppData\Local\Programs\BeMusicSeeker\BeMusicSeeker.exe`
  - COFF timestamp:
    - `BeMusicSeeker.exe`: `2020/03/09 17:54:22 (UTC)`
    - `IniLibrary.dll`: `2020/03/09 17:47:42 (UTC)`
  - CodeView PDB path:
    `D:\Sync\Repository\BeMusicSeeker\IniLibrary\obj\Release\IniLibrary.pdb`
- 同系譜証跡として `sqlite.net.dll` も確認:
  - COFF timestamp: `2020/03/09 17:47:42 (UTC)`
  - CodeView PDB path:
    `D:\Sync\Repository\BeMusicSeeker\SQLiteDotNET\obj\Release\sqlite.net.pdb`

判定更新案:

- **Red -> Green (保守者判断で可)**  
理由: fork元原著者ビルド由来と判断できる証跡を確認し、保守者責任で受け入れる方針を確定。

補足:

- 厳密な公開配布元URL/ソース照合は未完でも、プロジェクト方針として Green 化。
- 証跡は `third_party/licenses/Fork-Origin-Provenance-Notice.txt` に記録。

### 6-4. `commodore-20rounded-20v1.2.ttf`

実行結果:

- 現物SHA256: `D4070C0FC04BB1F53C38389027CBA56BCAEDF7BFA063D3B6D8EE533A77E99DD7`
- `https://www.dafont.com/commodore-64.font` 取得成功。
  - ページ上で作者 `Devin Cook`、`100% Free` 表示を確認。
  - ただし再配布許諾の法的条項本文は同ページから確定できず。
- フォントバイナリ文字列から `By Devin Cook (DevinCook.com)`、`1.2` を確認。

判定更新案:

- **Red -> Yellow (条件付きで可)**  
理由: 作者・配布ページ・free表示までは一次情報で追えたが、再配布条項本文が未確定。

Green化条件:

1. 元配布アーカイブ内 `license/readme` で再配布許諾条項を確認。
2. もしくは作者サイト等で明示的再配布許諾を取得。
