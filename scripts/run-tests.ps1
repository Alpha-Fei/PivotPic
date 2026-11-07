#Requires -Version 5.1
<#
.SYNOPSIS
    枢图 PivotPic 一键回归测试（真正的执行体，由 run-tests.cmd 启动）。
.DESCRIPTION
    依次执行 tests\ServiceTests 与 tests\ImageSmoke 两个控制台测试工程，
    任一失败立即中止并返回退出码 1。
    为什么用 PowerShell 而不是纯批处理：cmd.exe 解析含中文的 .cmd 会丢失字节同步
    （实测会把 dotnet.exe 截成 et.exe），中文输出交给 PowerShell 才稳定。
.PARAMETER DryRun
    只打印将要执行的命令，不真正跑测试。用于验证脚本本身的流程。
#>
[CmdletBinding()]
param(
    [switch]$DryRun
)

# 中文输出说明：run-tests.cmd 已执行 chcp 65001，PowerShell 会继承控制台代码页，
# 中文可直接正确显示。这里刻意不再改 [Console]::OutputEncoding —— 实测在
# Windows PowerShell 5.1 下修改它会导致 Write-Host 丢失换行、整段输出粘连在一起。

$ErrorActionPreference = 'Continue'

# scripts\ 的上一级就是仓库根目录。
$repoRoot = Split-Path -Parent $PSScriptRoot

# 解析 dotnet.exe：不绑定本机目录结构，按「DOTNET_ROOT → 用户级 SDK → PATH」依次尝试，
# 这样交付到标准安装（dotnet 在 PATH 上）的机器也能直接跑。
function Resolve-DotnetExe {
    $candidates = @()
    if ($env:DOTNET_ROOT) {
        $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet.exe')
    }
    $candidates += (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    $onPath = Get-Command 'dotnet.exe' -ErrorAction SilentlyContinue
    if ($null -ne $onPath) {
        return $onPath.Source
    }

    return $null
}

$dotnetExe = Resolve-DotnetExe

$separatorHeavy = '=' * 64
$separatorLight = '-' * 64

$suites = @(
    [pscustomobject]@{
        Name     = 'ServiceTests 服务层'
        Relative = 'tests\ServiceTests\ServiceTests.csproj'
        Count    = '44 项'
    },
    [pscustomobject]@{
        Name     = 'ImageSmoke 核心旋转（真实图片）'
        Relative = 'tests\ImageSmoke\ImageSmoke.csproj'
        Count    = '15 项'
    }
)

Write-Host $separatorHeavy
Write-Host '  枢图 PivotPic 回归测试'
Write-Host "  仓库根目录：$repoRoot"
if ($dotnetExe) {
    Write-Host "  SDK 目录：$dotnetExe"
} else {
    Write-Host '  SDK 目录：（未找到）'
}
if ($DryRun) {
    Write-Host '  模式：DryRun（只打印命令，不实际执行）'
}
Write-Host $separatorHeavy
Write-Host

# ---------------- 环境自检 ----------------
if (-not $dotnetExe) {
    Write-Host '[X] 未找到 .NET SDK：已依次尝试 DOTNET_ROOT、%USERPROFILE%\.dotnet 与 PATH 上的 dotnet.exe。'
    Write-Host '    请安装 .NET 8 SDK，或设置 DOTNET_ROOT 指向 SDK 目录。'
    Write-Host
    Write-Host $separatorHeavy
    Write-Host
    Write-Host '❌ 存在失败项'
    Write-Host
    Write-Host $separatorHeavy
    exit 1
}

$version = & $dotnetExe --version 2>$null
Write-Host "  SDK 版本：$version"
Write-Host

$failed = $false

foreach ($suite in $suites) {
    $project = Join-Path $repoRoot $suite.Relative

    Write-Host $separatorLight
    Write-Host "▶ 开始执行 $($suite.Name)（$($suite.Count)）"
    Write-Host "   $($suite.Relative)"
    Write-Host $separatorLight
    Write-Host

    if (-not (Test-Path -LiteralPath $project)) {
        Write-Host "[X] 找不到工程文件：$project"
        $failed = $true
        break
    }

    if ($DryRun) {
        Write-Host "[DryRun] 将执行：dotnet run --project `"$project`" -v q --nologo"
        Write-Host
        Write-Host "[DryRun] 跳过 $($suite.Name) 的实际执行。"
        Write-Host
        continue
    }

    & $dotnetExe run --project $project -v q --nologo
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0) {
        Write-Host
        Write-Host "[X] $($suite.Name) 存在失败项（退出码 $exitCode），已中止后续用例。"
        $failed = $true
        break
    }

    Write-Host
    Write-Host "[OK] $($suite.Name) 全部通过。"
    Write-Host
}

Write-Host $separatorHeavy
Write-Host

if ($failed) {
    Write-Host '❌ 存在失败项'
    Write-Host
    Write-Host $separatorHeavy
    exit 1
}

Write-Host '✅ 全部回归通过'
Write-Host
Write-Host $separatorHeavy
exit 0
