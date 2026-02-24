<#
.SYNOPSIS
    公開用リポジトリでのコミット、タグ付け、および GitHub リリースの作成を自動化するスクリプト

.DESCRIPTION
    1. gh コマンドのログイン状態を確認
    2. 公開用リポジトリに移動
    3. version.txt からバージョンを取得
    4. リリース用 zip パッケージの存在確認
    5. 未コミットの変更があればコミット
    6. タグ（vX.X.X.X）の作成と Push
    7. GitHub CLI (gh) を用いてリリースを作成
#>
$ErrorActionPreference = "Stop"

$pubRoot = "D:\github\bemusicseeker-unofficial-fork"

Write-Host "=== ステップ 0: 前提条件の確認 ===" -ForegroundColor Cyan

# gh コマンドの確認
Write-Host "  gh コマンドの認証状態を確認中..."
try {
    # 標準出力を捨てて終了コードのみを見る
    gh auth status 2>&1 | Out-Null
}
catch {
    Write-Host "エラー: gh コマンドがインストールされていないか、認証されていません。" -ForegroundColor Red
    Write-Host "事前に 'gh auth login' を実行してログインしてください。" -ForegroundColor Yellow
    exit 1
}
Write-Host "  gh 認証 OK" -ForegroundColor Green

# 公開用リポジトリへの移動
if (-not (Test-Path $pubRoot)) {
    Write-Host "エラー: 公開用リポジトリが見つかりません: $pubRoot" -ForegroundColor Red
    exit 1
}
Push-Location $pubRoot
Write-Host "  作業ディレクトリ: $PWD"

try {
    Write-Host "`n=== ステップ 1: バージョンとパッケージの確認 ===" -ForegroundColor Cyan

    # version.txt の読み取り
    if (-not (Test-Path "version.txt")) {
        throw "version.txt が見つかりません。"
    }
    $version = (Get-Content "version.txt" -Raw).Trim()
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "version.txt が空です。"
    }
    $tag = "v$version"
    Write-Host "  対象バージョン: $tag"

    # パッケージの確認
    $zipName = "bemusicseeker-unofficial-fork-${tag}.zip"
    $zipPath = Join-Path "dist" $zipName
    if (-not (Test-Path $zipPath)) {
        throw "リリース用パッケージが見つかりません: $zipPath `n事前に publish.ps1 を実行してパッケージを作成してください。"
    }
    Write-Host "  パッケージ確認 OK: $zipPath" -ForegroundColor Green


    Write-Host "`n=== ステップ 2: コミットとタグの作成 ===" -ForegroundColor Cyan

    # 変更をステージ
    git add .

    # 未コミットの変更があるか確認
    $hasChanges = (git status --porcelain) -ne $null
    
    if ($hasChanges) {
        Write-Host "  変更をコミットします..."
        git commit -m "Release $tag"
        Write-Host "  コミット完了" -ForegroundColor Green
    }
    else {
        Write-Host "  コミットする変更はありません（スキップ）" -ForegroundColor Yellow
    }

    # タグの存在確認
    $existingTag = git tag -l $tag
    if ($existingTag) {
        Write-Host "  タグ $tag はすでに存在します（タグ付けをスキップ）" -ForegroundColor Yellow
    }
    else {
        Write-Host "  タグ $tag を作成します..."
        git tag $tag
        Write-Host "  タグ作成完了" -ForegroundColor Green
    }


    Write-Host "`n=== ステップ 3: リモートへの Push ===" -ForegroundColor Cyan
    Write-Host "  現在のブランチとタグを Push します..."
    git push origin HEAD
    git push origin $tag
    Write-Host "  Push 完了" -ForegroundColor Green


    Write-Host "`n=== ステップ 4: GitHub リリースの作成 ===" -ForegroundColor Cyan
    
    # すでにリリースが存在するか確認
    $releaseExists = $false
    try {
        gh release view $tag 2>&1 | Out-Null
        $releaseExists = $true
    }
    catch {
        # エラーになる（=リリースが存在しない）場合は正常フロー
    }

    if ($releaseExists) {
        Write-Host "  リリース $tag はすでに存在します（作成をスキップ）" -ForegroundColor Yellow
    }
    else {
        Write-Host "  GitHub リリースを作成します..."
        gh release create $tag $zipPath --title $tag --generate-notes
        Write-Host "  リリース作成完了" -ForegroundColor Green
    }

    Write-Host "`n=== 全てのリリース処理が完了しました ===" -ForegroundColor Green

}
finally {
    # 実行元のディレクトリに戻る
    Pop-Location
}
