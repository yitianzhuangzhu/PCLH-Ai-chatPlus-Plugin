<#
.SYNOPSIS
    本仓库的插件编译入口：定位启动器 exe、插件 SDK 与本机工具链，一条命令编译（可选部署 / 打包）。

.DESCRIPTION
    对 SDK 自带 build.ps1 的薄封装，省掉每次手写三个路径参数。默认路径约定：
    - 启动器引用：<仓库根>\PCL-Harness.exe
    - 插件 SDK  ：<仓库根>\PCL-Harness-Plugin-SDK-*（自动探测）
    - 编译器    ：.tools\roslyn\tasks\net472\vbc.exe（NuGet Microsoft.Net.Compilers.Toolset 4.14.0）
    - 参考程序集：.tools\refasm\build\.NETFramework\v4.8（NuGet Microsoft.NETFramework.ReferenceAssemblies.net48 1.0.3）

    这几样都不入库，缺了脚本会直接报出来，取法见 README「自行编译」。

.PARAMETER Project
    插件目录（含 plugin.json 与 .vb），默认 main。

.PARAMETER Deploy
    编译后把插件复制到启动器实际使用的 Plugins\<插件id>\。新插件仍需在插件页点「启用」。

.PARAMETER Pack
    编译后打成 dist\<id>-<version>.pclplugin。

.PARAMETER PluginRoot
    手动指定插件根目录（即含 plugins.json 的那层）。不填则自动探测：
    先看正在运行的 PCL-Harness 进程所在目录下的 PCLHarness\Plugins，
    再退回 <仓库根>\PCLHarness\Plugins。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build-plugin.ps1 -Project main -Pack
#>
[CmdletBinding()]
param(
    [string]$Project = "main",
    [switch]$Deploy,
    [switch]$Pack,
    [string]$PluginRoot = ""
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# ---------- 定位 SDK ----------
$sdk = Get-ChildItem -LiteralPath $root -Directory -Filter "PCL-Harness-Plugin-SDK-*" -ErrorAction SilentlyContinue |
       Sort-Object Name -Descending | Select-Object -First 1
if (-not $sdk) {
    throw "找不到插件 SDK。请把 PCL-Harness-Plugin-SDK-* 解压到 $root（见 README「自行编译」）。"
}
$sdk = $sdk.FullName
$buildScript = Join-Path $sdk "build.ps1"

# ---------- 定位启动器 ----------
$launcher = Join-Path $root "PCL-Harness.exe"
if (-not (Test-Path -LiteralPath $launcher)) {
    $parentExe = Join-Path (Split-Path $root -Parent) "PCL-Harness.exe"
    if (Test-Path -LiteralPath $parentExe) { $launcher = $parentExe }
}

# ---------- 定位工具链 ----------
$vbc = Join-Path $root ".tools\roslyn\tasks\net472\vbc.exe"
$refAsm = Join-Path $root ".tools\refasm\build\.NETFramework\v4.8"

foreach ($p in @($buildScript, $launcher, $vbc, (Join-Path $refAsm "mscorlib.dll"))) {
    if (-not (Test-Path -LiteralPath $p)) { throw "缺少前置条件: $p（见 README「自行编译」）" }
}

$projDir = if ([System.IO.Path]::IsPathRooted($Project)) { $Project } else { Join-Path $root $Project }
if (-not (Test-Path -LiteralPath $projDir)) { throw "找不到插件目录: $projDir" }

# 宿主内部 API 用到 PCLCS.McComponents，而 PCLCS / Newtonsoft.Json 在启动器里是嵌入资源、磁盘上没有，
# 需要先用 .tools\extract 从 exe 导出到 .tools\libs；MeloongCore 是宿主运行库，编译期要能解析签名。
$libsDir = Join-Path $root ".tools\libs-new"
if (-not (Test-Path -LiteralPath $libsDir)) { $libsDir = Join-Path $root ".tools\libs" }
$extraRefs = @()
foreach ($n in @("PCLCS.dll", "Newtonsoft.Json.dll", "MeloongCore.dll", "MeloongCore.Wpf.dll")) {
    $p = Join-Path $libsDir $n
    if (Test-Path -LiteralPath $p) { $extraRefs += $p }
}

$buildArgs = @{
    Project       = $projDir
    Launcher      = $launcher
    VbcPath       = $vbc
    ReferencePath = $refAsm
}
if ($extraRefs.Count -gt 0) { $buildArgs["ExtraReference"] = $extraRefs }

& $buildScript @buildArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败（exit $LASTEXITCODE）" }

$manifest = Get-Content (Join-Path $projDir "plugin.json") -Raw -Encoding UTF8 | ConvertFrom-Json

# ---------- 打包 ----------
if ($Pack) {
    $packScript = Join-Path $sdk "pack-plugin.ps1"
    if (-not (Test-Path -LiteralPath $packScript)) { throw "找不到 $packScript" }
    $distDir = Join-Path $root "dist"
    if (-not (Test-Path -LiteralPath $distDir)) { New-Item -ItemType Directory -Path $distDir -Force | Out-Null }
    $ver = if ($manifest.version) { [string]$manifest.version } else { "0.0.0" }
    $out = Join-Path $distDir ("{0}-{1}.pclplugin" -f $manifest.id, $ver)

    # 只把运行期文件交给打包器：源码（.vb）不该进发布包
    $stage = Join-Path ([System.IO.Path]::GetTempPath()) ("pclplugin-stage-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    try {
        Get-ChildItem $projDir -File |
            Where-Object { $_.Name -eq "plugin.json" -or $_.Extension -in @(".dll", ".png", ".ico") } |
            ForEach-Object { Copy-Item $_.FullName -Destination $stage -Force }
        & $packScript -Project $stage -Output $out
        if ($LASTEXITCODE -ne 0) { throw "打包失败（exit $LASTEXITCODE）" }
    }
    finally {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if (-not $Deploy) {
    Write-Host "（未部署；加 -Deploy 可复制到 PCLHarness\Plugins\）"
    exit 0
}

# ---------- 部署 ----------
# 插件根目录：启动器把数据放在 <exe目录>\PCLHarness\，不同机器上 exe 可能在别的盘。
$pluginsDir = ""
if ($PluginRoot) {
    $pluginsDir = $PluginRoot
} else {
    $proc = Get-Process -Name "PCL-Harness", "PCL Harness" -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and (Test-Path $_.Path) } | Select-Object -First 1
    if ($proc) {
        $fromProc = Join-Path (Split-Path -Parent $proc.Path) "PCLHarness\Plugins"
        if (Test-Path $fromProc) { $pluginsDir = $fromProc }
    }
    if (-not $pluginsDir) {
        # 退一步：找本机已知的、已经扫过插件的目录（有 plugins.json 说明启动器用过）
        $candidates = @()
        foreach ($drive in (Get-PSDrive -PSProvider FileSystem).Root) {
            $candidates += (Join-Path $drive "MC\PCLHarness\Plugins")
        }
        $candidates += (Join-Path $root "PCLHarness\Plugins")
        $hit = $candidates | Where-Object { Test-Path (Join-Path $_ "plugins.json") } | Select-Object -First 1
        if ($hit) { $pluginsDir = $hit }
    }
    if (-not $pluginsDir) { $pluginsDir = Join-Path $root "PCLHarness\Plugins" }
}

Write-Host "插件根目录: $pluginsDir"
if (-not (Test-Path $pluginsDir)) {
    Write-Host "警告：这个目录还不存在，请先启动一次启动器（插件页会显示真实路径）。" -ForegroundColor Yellow
}

$target = Join-Path $pluginsDir $manifest.id
Write-Host "部署到: $target"

if (-not (Test-Path $target)) { New-Item -ItemType Directory -Path $target -Force | Out-Null }

# 只复制运行期需要的文件：清单、dll、图标；源码 / README 不同步
Get-ChildItem $projDir -File |
    Where-Object { $_.Name -eq "plugin.json" -or $_.Extension -in @(".dll", ".png", ".ico") } |
    ForEach-Object { Copy-Item $_.FullName -Destination $target -Force }

Write-Host "完成。到启动器「插件」页点「重新扫描」，再点该插件的「启用」（首次启用会弹权限确认）。" -ForegroundColor Green
