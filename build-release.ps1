# 正画 PivotPic 一键出包：按迭代计数递增版本号 → 发布主程序 → 打包安装器
# 用法：powershell -ExecutionPolicy Bypass -File build-release.ps1
#
# 安装版：主程序与依赖 DLL 以普通文件形式放进安装目录（不再打成单文件 exe），
#         安装器内嵌整份目录的 zip，安装时解压即可。
# 绿色版：仍然输出单文件 exe，方便拷贝即用。

param(
    [switch]$SkipCounter
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$dotnet = $null
$cmd = Get-Command dotnet -ErrorAction SilentlyContinue
if ($cmd) { $dotnet = $cmd.Source }
if (-not $dotnet) {
    $candidate = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if (Test-Path $candidate) { $dotnet = $candidate } else { throw '未找到 dotnet，请先安装 .NET 8 SDK。' }
}

# ---------- 1) 版本号：主.次.迭代次数 ----------
$counterFile = Join-Path $root 'version-iteration.txt'
$iteration = 1
if (Test-Path $counterFile) {
    $raw = (Get-Content $counterFile -Raw).Trim()
    if ($raw -match '^\d+$') { $iteration = [int]$raw }
    if (-not $SkipCounter) { $iteration++ }
}
Set-Content -Path $counterFile -Value $iteration -Encoding ascii

$version = "1.3.$iteration"
Write-Host "==> 版本 $version" -ForegroundColor Cyan

# ---------- 2) 发布主程序（多文件，供安装版使用） ----------
Write-Host '==> 发布主程序（多文件）' -ForegroundColor Cyan
if (Test-Path 'dist/app') { Remove-Item 'dist/app' -Recurse -Force }
& $dotnet publish src/PivotPic/PivotPic.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:Version=$version -o dist/app
if ($LASTEXITCODE -ne 0) { throw '主程序发布失败' }

# ---------- 2.5) 构建独立卸载程序，放进安装目录 ----------
Write-Host '==> 构建卸载程序' -ForegroundColor Cyan
if (Test-Path 'dist/uninst') { Remove-Item 'dist/uninst' -Recurse -Force }
& $dotnet publish src/PivotPic.Uninstaller/PivotPic.Uninstaller.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:Version=$version -o dist/uninst
if ($LASTEXITCODE -ne 0) { throw '卸载程序构建失败' }

Copy-Item (Join-Path $root 'dist/uninst/uninstall.exe') (Join-Path $root 'dist/app/uninstall.exe') -Force

# ---------- 2.6) 放入 libvips 运行库（大图低内存旋转） ----------
$vipsSrc = Join-Path $root 'third_party/vips/libvips-42.dll'
if (Test-Path $vipsSrc) {
    $vipsDst = Join-Path $root 'dist/app/vips'
    New-Item -ItemType Directory -Force -Path $vipsDst | Out-Null
    Copy-Item $vipsSrc (Join-Path $vipsDst 'libvips-42.dll') -Force
    Copy-Item (Join-Path $root 'third_party/vips/README-LGPL.txt') $vipsDst -Force
    Copy-Item (Join-Path $root 'third_party/vips/versions.json') $vipsDst -Force
    Write-Host "    libvips: $([math]::Round((Get-Item $vipsSrc).Length / 1MB, 1)) MB"
}
else {
    Write-Host '    [警告] 未找到 third_party/vips/libvips-42.dll，大图将回退 WPF' -ForegroundColor Yellow
}

# ---------- 2.7) 放入内置加速组件（GitHub 加速，纯透传） ----------
# 缺失只警告不失败：没有它程序会静默降级为直连，核心功能（旋转/监控）完全不受影响。
# 许可证与 NOTICE 必须一起带走，否则违反 Apache-2.0 第 4 条。
$gitportSrc = Join-Path $root 'third_party/gitport'
if (Test-Path (Join-Path $gitportSrc 'gitport-github.exe')) {
    $proxyDst = Join-Path $root 'dist/app/proxy'
    New-Item -ItemType Directory -Force -Path $proxyDst | Out-Null
    Copy-Item (Join-Path $gitportSrc 'gitport-github.exe') $proxyDst -Force

    foreach ($sidecar in @('LICENSE-APACHE-2.0.txt', 'NOTICE.txt', 'version.json')) {
        $srcFile = Join-Path $gitportSrc $sidecar
        if (Test-Path $srcFile) {
            Copy-Item $srcFile $proxyDst -Force
        }
        else {
            Write-Host "    [警告] 缺少 $sidecar（许可合规文件不完整）" -ForegroundColor Yellow
        }
    }

    $proxyMb = [math]::Round((Get-Item (Join-Path $gitportSrc 'gitport-github.exe')).Length / 1MB, 1)
    Write-Host "    内置加速组件: $proxyMb MB"
}
else {
    Write-Host '    [警告] 未找到 third_party/gitport/gitport-github.exe，在线更新将始终走直连' -ForegroundColor Yellow
}

# ---------- 3) 准备安装器载荷：整份目录压成 zip ----------
Write-Host '==> 打包安装载荷' -ForegroundColor Cyan
$payload = Join-Path $root 'src/PivotPic.Setup/payload'
New-Item -ItemType Directory -Force -Path $payload | Out-Null
Remove-Item (Join-Path $payload '*.zip') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $payload 'PivotPic.exe') -Force -ErrorAction SilentlyContinue

$zipPath = Join-Path $payload 'app.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    (Join-Path $root 'dist/app'),
    $zipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)

Copy-Item (Join-Path $root 'src/PivotPic/Assets/brand-256.png') (Join-Path $payload 'logo.png') -Force

$zipMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "    载荷 app.zip = $zipMb MB"

# ---------- 4) 发布安装器 ----------
Write-Host '==> 打包安装器' -ForegroundColor Cyan
& $dotnet publish src/PivotPic.Setup/PivotPic.Setup.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$version -o dist/installer
if ($LASTEXITCODE -ne 0) { throw '安装器打包失败' }

# ---------- 5) 输出安装包 ----------
$setupExe = Join-Path $root 'dist/installer/正画安装程序.exe'
# 安装包统一用 ASCII 文件名：GitHub Release 资产名会被剥离非 ASCII 字符
    # （实测"正画-Setup-1.3.0.exe"上传后变成"-Setup-1.3.0.exe"），而自动更新要按资产名
    # 精确比对 checksums.txt，命名必须唯一。安装包内的品牌与窗口标题不受影响。
    $finalExe = Join-Path $root "dist/PivotPic-Setup-$version.exe"
Copy-Item $setupExe $finalExe -Force

$sizeMb = [math]::Round((Get-Item $finalExe).Length / 1MB, 1)
Write-Host "==> 安装包：$finalExe ($sizeMb MB)" -ForegroundColor Green

# ---------- 6) 绿色版：单文件 exe ----------
Write-Host '==> 发布绿色版（单文件）' -ForegroundColor Cyan
& $dotnet publish src/PivotPic/PivotPic.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$version -o dist/green
if ($LASTEXITCODE -ne 0) { throw '绿色版发布失败' }

try {
    Copy-Item (Join-Path $root 'dist/green/PivotPic.exe') (Join-Path $root 'dist/PivotPic.exe') -Force

    # 绿色版同样需要 vips 目录（大图低内存旋转）
    if (Test-Path $vipsSrc) {
        $greenVips = Join-Path $root 'dist/vips'
        New-Item -ItemType Directory -Force -Path $greenVips | Out-Null
        Copy-Item $vipsSrc (Join-Path $greenVips 'libvips-42.dll') -Force
        Copy-Item (Join-Path $root 'third_party/vips/README-LGPL.txt') $greenVips -Force
    }

    # 绿色版同样需要内置加速组件（缺了也能跑，只是更新走直连）
    if (Test-Path (Join-Path $gitportSrc 'gitport-github.exe')) {
        $greenProxy = Join-Path $root 'dist/proxy'
        New-Item -ItemType Directory -Force -Path $greenProxy | Out-Null
        Copy-Item (Join-Path $gitportSrc 'gitport-github.exe') $greenProxy -Force
        foreach ($sidecar in @('LICENSE-APACHE-2.0.txt', 'NOTICE.txt', 'version.json')) {
            $srcFile = Join-Path $gitportSrc $sidecar
            if (Test-Path $srcFile) { Copy-Item $srcFile $greenProxy -Force }
        }
    }
    Write-Host "==> 绿色版：dist\PivotPic.exe" -ForegroundColor Green
}
catch {
    Write-Host "==> 绿色版未更新（PivotPic.exe 正在运行），关闭后重跑即可" -ForegroundColor Yellow
}

# ---------- 6) 生成 Release 校验清单（自动更新会拉这个文件比对 SHA256） ----------
Write-Host '==> 生成 checksums.txt' -ForegroundColor Cyan
$checksumTargets = Get-ChildItem (Join-Path $root 'dist') -Filter '*Setup*.exe' -File |
    Where-Object { $_.Name -like "*$version*" }
if ($checksumTargets) {
    $lines = foreach ($file in $checksumTargets) {
        $hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($file.Name)"
    }
    $checksumPath = Join-Path $root 'dist/checksums.txt'
    # Windows PowerShell 5.1 没有 utf8NoBOM；文件名含中文，既不能写 ASCII 也不能带 BOM（会把 BOM 混进首行哈希）
    [System.IO.File]::WriteAllLines(
        $checksumPath,
        [string[]]$lines,
        (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "    dist\checksums.txt ($($checksumTargets.Count) 个文件)"
}
else {
    Write-Host '    [警告] 未找到本版本的安装包，跳过 checksums.txt' -ForegroundColor Yellow
}

Write-Host ''
Write-Host "==> 完成，版本 $version" -ForegroundColor Green
