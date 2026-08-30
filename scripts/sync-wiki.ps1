# ==============================================================================
# BBDown Wiki 一键同步脚本 (PowerShell)
# 功能: 将 docs/wiki/ 目录下的所有 Markdown 文档同步推送至 GitHub Wiki 仓库
# 语义: docs/wiki/ 是 wiki 的唯一权威来源——本地已删除的页面在 wiki 上同样会被移除
# ==============================================================================

$ErrorActionPreference = "Stop"

$repoOwner = "aliveranme"
$repoName = "BBDown"
$wikiGitUrl = "https://github.com/$repoOwner/$repoName.wiki.git"
$wikiSourceDir = Join-Path $PSScriptRoot "..\docs\wiki"
$tempWikiDir = Join-Path $env:TEMP "BBDown_Wiki_Sync"

# 外部命令（git）不抛 PowerShell 异常，只能靠 $LASTEXITCODE 判断成败；
# 统一在失败时抛错终结，不再依赖误导性的 try/catch。
function Assert-GitSucceeded([string]$Action) {
    if ($LASTEXITCODE -ne 0) {
        throw "git $Action 失败 (exit=$LASTEXITCODE)"
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

try {
    Write-Host ">>> 正在准备同步 Wiki 文档..." -ForegroundColor Cyan

    # 1. 确保源目录存在
    if (-not (Test-Path $wikiSourceDir)) {
        throw "错误: 找不到 Wiki 源文档目录: $wikiSourceDir"
    }

    # 2. 清理临时目录
    if (Test-Path $tempWikiDir) {
        Remove-Item -Recurse -Force $tempWikiDir
    }

    # 3. 克隆 Wiki 仓库（检查 $LASTEXITCODE；克隆失败回退到初始化本地仓库再推送）
    Write-Host ">>> 正在连接 GitHub Wiki 仓库: $wikiGitUrl ..." -ForegroundColor Cyan
    git clone --quiet $wikiGitUrl $tempWikiDir 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Host ">>> Wiki 仓库尚未在 GitHub 上初始化，创建本地仓库并推送..." -ForegroundColor Yellow
        New-Item -ItemType Directory -Force -Path $tempWikiDir | Out-Null
        Push-Location $tempWikiDir
        git init -b master; Assert-GitSucceeded "init"
        git remote add origin $wikiGitUrl; Assert-GitSucceeded "remote add"
    } else {
        Push-Location $tempWikiDir
    }

    # 4. 拷贝最新文档
    Write-Host ">>> 复制 docs/wiki/ 到暂存区..." -ForegroundColor Cyan
    Copy-Item -Path (Join-Path $wikiSourceDir "*") -Destination $tempWikiDir -Force

    # 4.1 清理 wiki 上存在但源目录已删除的页面（docs/wiki 是权威来源；
    #     只删 *.md，不触碰 .git）
    $stale = Get-ChildItem -Path $tempWikiDir -Filter *.md -File |
        Where-Object { -not (Test-Path (Join-Path $wikiSourceDir $_.Name)) }
    foreach ($f in $stale) {
        Write-Host ">>> 移除 wiki 上已废弃的页面: $($f.Name)" -ForegroundColor Yellow
        Remove-Item -Force $f.FullName
    }

    # 5. 提交并推送（commit 成败必须检查，否则推送旧内容仍走"成功"路径）
    git add .
    $status = git status --porcelain
    if ([string]::IsNullOrWhiteSpace($status)) {
        Write-Host ">>> Wiki 文档内容无变更，无需推送。" -ForegroundColor Green
    } else {
        git commit -m "docs(wiki): sync wiki documentation from repository"
        Assert-GitSucceeded "commit"
        Write-Host ">>> 正在推送到 GitHub Wiki..." -ForegroundColor Cyan
        git push -u origin master
        if ($LASTEXITCODE -eq 0) {
            Write-Host ">>> Wiki 同步成功！访问地址: https://github.com/$repoOwner/$repoName/wiki" -ForegroundColor Green
        } else {
            Write-Host ">>> 推送失败。如果是首次创建 Wiki，请先前往浏览器访问 https://github.com/$repoOwner/$repoName/wiki 点击一次 'Create the first page'，然后再运行本脚本。" -ForegroundColor Yellow
            exit 1
        }
    }
}
finally {
    # 无论成败都回到仓库根目录，避免调用者会话被留在临时目录；
    # 临时目录保留便于排查，下次运行开头会自动清理
    Set-Location $repoRoot
}
