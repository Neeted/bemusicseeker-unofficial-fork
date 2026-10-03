# Linuxでの開発環境の構築

Linux x64でソースを編集し、Windows向けのクロスビルドと、Windows依存のない既存テストの局所検証を行う手順です。通常の開発環境と最終検証は[Windowsの構築手順](setup.md)と[検証仕様](spec/development/testing.md)に従います。

LinuxでWPFアプリを実行する手順ではありません。クロスビルドの成功はWindows上の実行、画面、再生、ネイティブDLL、更新・配布、NativeAOTの成功を保証しません。Linuxの確認を`Functional` / `Full`の成功として報告しません。

## 前提

- Gitで開発用ソース一式をクローンし、以下はリポジトリルートのBashで実行します。取得先とブランチは[開発環境の構築](setup.md)を参照します。
- glibcを使用するLinux x64が対象です。ARM64やAlpineなどのmusl環境はこのスクリプトの対象外です。
- Bash、Python 3、curl、tar、gzip、sha512sumと、通常のLinux基本コマンド（uname、mkdir、mktemp、rm、mv）が必要です。不足時はスクリプトが失敗します。
- .NETの実行にはglibc、libgcc、libstdc++、ICU、OpenSSL、zlib、Kerberos関連ライブラリなど、ディストリビューションに対応した依存関係とCA証明書が必要です。[MicrosoftのLinux導入資料](https://learn.microsoft.com/en-us/dotnet/core/install/linux-scripted-manual#dependencies)から使用するディストリビューションの要件を確認し、不足分は管理者が準備します。
- 初回のSDK取得とNuGet復元にはHTTPSで公式配布元と設定済みNuGetソースへ接続できる必要があります。SDK・パッケージ・出力を置く場所には書込み権限が必要です。

スクリプトは`sudo`を使わず、OS設定、システムのSDK、シェルのプロファイルを変更しません。Windows用のDLLをLinux用の実装やスタブで置き換えません。

## SDKとキャッシュの準備

必要なSDKは[global.json](../global.json)の`version`と`rollForward`を正本とします。選択規則をスクリプトで再実装せず、リポジトリルートでの`dotnet --version`で確認します。条件を満たす既存SDKがあれば再利用し、なければ指定された正確な版の公式Linux x64 SDKを導入します。指定や追跡対象のパッケージロックを変更して回避しません。

```bash
bash scripts/linux/bootstrap.sh
source scripts/linux/environment.sh
dotnet --version
```

`bootstrap.sh`は公式リリースメタデータから指定版のURLとSHA-512を取得し、一時領域へダウンロードします。ハッシュ、展開、リポジトリルートでのSDK選択を確認してからローカル配置先へ移します。取得・照合・展開・起動の失敗は非ゼロ終了とし、既存SDKを上書きしません。不完全な配置先が残っている場合は自動で削除せず、原因と内容を確認してからその配置先だけを除去して再実行します。

既定の保存先はGit管理対象外の`.tmp/linux-toolchain/`です。

| 保存先 | 用途 |
| --- | --- |
| `dotnet/<version>/` | スクリプトが導入したSDK。既存SDKを再利用した場合は作らない。 |
| `cli-home/` | `DOTNET_CLI_HOME`。CLIの初回実行データなど。 |
| `nuget-packages/` | `NUGET_PACKAGES`。復元したパッケージ。 |
| `nuget-http-cache/` | `NUGET_HTTP_CACHE_PATH`。NuGetのHTTPキャッシュ。 |
| `nuget-scratch/` | `NUGET_SCRATCH`。NuGetの一時作業領域。 |
| `nuget-plugins-cache/` | `NUGET_PLUGINS_CACHE_PATH`。NuGetプラグインのキャッシュ。 |

変更する場合は、初回構築と以後の各シェルで同じ絶対パスを指定します。空白を含むパスも引用符で囲みます。

```bash
export BMS_LINUX_TOOLCHAIN_DIR="$HOME/.cache/bemusicseeker/linux-toolchain"
bash scripts/linux/bootstrap.sh
source scripts/linux/environment.sh
```

指定先は書込み可能である必要があります。`environment.sh`は現在のシェルに上記キャッシュ変数を設定します。PATH上のSDKが選択可能ならそのまま使い、選択できない場合だけ、起動できるローカルSDKのPATHと`DOTNET_ROOT`を設定します。SDKをダウンロードする処理はありません。繰返しsourceしてもPATHを重複追加せず、呼出元のシェルオプションや作業ディレクトリを変えません。新しいシェルでは再度sourceします。

初回のCLI実行による開発用HTTPS証明書の生成は、この用途に不要なため`DOTNET_GENERATE_ASPNET_CERTIFICATE=false`で無効にします。それ以外の利用者の設定は変更しません。

## Windows向けクロスビルド

復元とビルドの構成を揃え、追跡済みのロックファイルに従って復元します。

```bash
dotnet restore BeMusicSeeker.sln --locked-mode \
  -p:Configuration=Release -p:Platform=x64 -p:EnableWindowsTargeting=true -m:1
dotnet build BeMusicSeeker.sln --no-restore -c Release --disable-build-servers \
  -p:Platform=x64 -p:EnableWindowsTargeting=true -m:1 -p:UseSharedCompilation=false
```

これは本体、更新プログラム、Windows向け既存テスト、ソリューション内の補助ツールをコンパイルする確認です。`EnableWindowsTargeting`はWindows向け参照の取得を許可するもので、Linux上でWindowsDesktopランタイムやWPFを実行可能にはしません。発行やNativeAOTの検証は含みません。

例ではMSBuildを1ノードで実行し、ビルドサーバーと共有コンパイラを使いません。コマンド間で常駐プロセスを共有せずに確認する設定で、コンパイル対象や検査内容は減らしません。Windowsの標準入口の並列度や期限は変更しません。

## Windows依存のない既存テストを局所実行する

変更に関連する既存テストと、そのテストが実際に使う本番ソースを選び、一時プロジェクトへ直接リンクします。これはリンクしたソースの検証であり、本体アセンブリ全体の検証ではありません。WPF、Windows API、Windows専用ネイティブDLLなどに依存するテストはWindowsで実行します。依存する型や表明をスタブ・コピーへ置き換えて成功扱いにしません。

次は既存の`FractionTests`を実行する再現例です。現在のクラスには4テストがありますが、件数を固定した成功条件にはしません。実際の変更では関連する既存テストを選び直し、検出・実行された名前と範囲を確認します。この例だけで無関係な変更の検証を完了しません。

`.tmp/linux-tests/`がこの例専用であることを確認してから実行します。プロジェクト・ロック・出力・TRXはすべて一時領域に置き、Gitへ追加しません。パッケージ版はルートの[中央管理ファイル](../Directory.Packages.props)を継承します。

```bash
(
  set -euo pipefail
  test_project=.tmp/linux-tests/Existing.Tests.csproj
  mkdir -p .tmp/linux-tests
  cat > "$test_project" <<'XML'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14.0</LangVersion>
    <Nullable>enable</Nullable>
    <IsTestProject>true</IsTestProject>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="MSTest.TestAdapter" />
    <PackageReference Include="MSTest.TestFramework" />
    <Compile Include="../../BeMusicSeeker/Ribbit/Math/Fraction.cs" Link="Fraction.cs" />
    <Compile Include="../../BeMusicSeeker.Tests/Chart/FractionTests.cs" Link="FractionTests.cs" />
  </ItemGroup>
</Project>
XML
  # この一時プロジェクトだけの初回ロックを作る。既存ロックのある再実行では省く。
  if [[ ! -f .tmp/linux-tests/packages.lock.json ]]; then
    dotnet restore "$test_project" -p:Configuration=Release -p:Platform=x64
  fi
  dotnet restore "$test_project" --locked-mode -p:Configuration=Release -p:Platform=x64
  dotnet build "$test_project" --no-restore -c Release -p:Platform=x64
  rm -f -- .tmp/linux-tests/results/existing.trx
  dotnet test "$test_project" --no-build --no-restore -c Release -p:Platform=x64 \
    --logger 'trx;LogFileName=existing.trx' --results-directory "$PWD/.tmp/linux-tests/results"
  python3 - <<'PY'
import sys
import xml.etree.ElementTree as ET

root = ET.parse(".tmp/linux-tests/results/existing.trx").getroot()
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
results = root.findall(".//t:UnitTestResult", ns)
if not results or any(result.get("outcome") != "Passed" for result in results):
    sys.exit("TRX contains no tests or a result other than Passed")
print(f"Passed: {len(results)}")
for result in results:
    print(result.get("testName"))
PY
)
```

`dotnet test`の非ゼロ終了、テスト0件、TRX欠落、`Skipped` / `Inconclusive` / `NotExecuted`を含む`Passed`以外は成功ではありません。前回のTRXを再利用しないよう、実行前に当該ファイルを除去しています。ソース選択や中央管理の依存関係を変えた場合は、この一時プロジェクトのロックだけを内容確認の上で作り直します。追跡対象のロック不一致をこの手順で解消しません。

## 残る確認と引継ぎ

Linuxで行った復元・ビルドと、選択した既存テストの名前・件数・結果を報告し、Windowsで未実施の検証を明示します。通常の最終統合はWindowsの`Functional`、配布・更新・リリースは`Full`、画面や実再生などは対象に応じたWindows上の確認が必要です。詳細は[検証仕様](spec/development/testing.md)に従います。

補助CLIをLinux上で確認する場合も、`--help`の成功は引数入口の確認に限ります。実データを使う処理、ファイル形式、DB差分、Windows固有の挙動は、それぞれ対応する既存テストと実行条件で別途確認します。

SDKの起動に失敗したら、ランタイム依存ライブラリと`global.json`を確認します。復元が失敗したら、表示されたNuGetソース・接続・認証・ロック不一致を確認します。ビルドやテストの失敗は範囲を記録し、Windows依存による未実施とコードの不具合を区別します。設定の緩和、テスト除外、成功するまでの反復で完了扱いにしません。
