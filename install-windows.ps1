[CmdletBinding()]
param(
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'

$projectPath = Join-Path $PSScriptRoot 'RemoteAssistant.csproj'
$buildDirectory = Join-Path $PSScriptRoot '.build\windows-release'
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\RemoteAssistant'
$installedExecutable = Join-Path $installDirectory 'RemoteAssistant.exe'
$installMarker = Join-Path $installDirectory '.remote-assistant-install'
$legacyInstallDirectory = Join-Path $env:LOCALAPPDATA 'Programs\LunaDesktopHelper'
$legacyInstalledExecutable = Join-Path $legacyInstallDirectory 'LunaDesktopHelper.exe'
$legacyInstallMarker = Join-Path $legacyInstallDirectory '.luna-desktop-helper-install'
$desktopDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
$shortcutPath = Join-Path $desktopDirectory '远程助手.lnk'

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "找不到项目文件：$projectPath"
}

$expectedBuildDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '.build\windows-release'))
$actualBuildDirectory = [IO.Path]::GetFullPath($buildDirectory)
if (-not $actualBuildDirectory.Equals($expectedBuildDirectory, [StringComparison]::OrdinalIgnoreCase)) {
    throw "拒绝清理意外的构建目录：$actualBuildDirectory"
}

$expectedInstallDirectory = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\RemoteAssistant'))
$actualInstallDirectory = [IO.Path]::GetFullPath($installDirectory)
if (-not $actualInstallDirectory.Equals($expectedInstallDirectory, [StringComparison]::OrdinalIgnoreCase)) {
    throw "拒绝更新意外的安装目录：$actualInstallDirectory"
}

New-Item -ItemType Directory -Path $buildDirectory -Force | Out-Null
Get-ChildItem -LiteralPath $buildDirectory -Force | Remove-Item -Recurse -Force -ErrorAction Stop
New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null

dotnet publish $projectPath `
    --configuration Release `
    --output $buildDirectory `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Windows Release 发布失败，退出代码：$LASTEXITCODE"
}

$projectBuildRoot = Join-Path $PSScriptRoot '.build'
$replaceableProcesses = Get-Process -Name 'RemoteAssistant', 'LunaDesktopHelper' -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Path -eq $installedExecutable -or $_.Path -eq $legacyInstalledExecutable -or
        ($_.Path -and $_.Path.StartsWith($projectBuildRoot, [StringComparison]::OrdinalIgnoreCase))
    }
if ($replaceableProcesses) {
    $replaceableProcesses | Stop-Process
    $replaceableProcesses | Wait-Process -Timeout 10
}

$copyAttempts = 8
for ($attempt = 1; $attempt -le $copyAttempts; $attempt++) {
    try {
        $existingItems = @(Get-ChildItem -LiteralPath $installDirectory -Force)
        if ($existingItems.Count -gt 0 -and
            -not (Test-Path -LiteralPath $installMarker -PathType Leaf) -and
            -not (Test-Path -LiteralPath $installedExecutable -PathType Leaf)) {
            throw "安装目录包含无法确认来源的文件，拒绝覆盖：$installDirectory"
        }
        $existingItems | Remove-Item -Recurse -Force -ErrorAction Stop
        Copy-Item -Path (Join-Path $buildDirectory '*') -Destination $installDirectory -Recurse -Force -ErrorAction Stop
        New-Item -ItemType File -Path $installMarker -Force | Out-Null
        break
    }
    catch {
        if ($attempt -eq $copyAttempts) {
            throw
        }
        Start-Sleep -Milliseconds 500
    }
}
if (-not (Test-Path -LiteralPath $installedExecutable -PathType Leaf)) {
    throw "发布完成后未找到程序：$installedExecutable"
}

$expectedLegacyInstallDirectory = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\LunaDesktopHelper'))
$actualLegacyInstallDirectory = [IO.Path]::GetFullPath($legacyInstallDirectory)
if (Test-Path -LiteralPath $legacyInstallDirectory -PathType Container) {
    if (-not $actualLegacyInstallDirectory.Equals($expectedLegacyInstallDirectory, [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝清理意外的旧安装目录：$actualLegacyInstallDirectory"
    }
    if ((Test-Path -LiteralPath $legacyInstallMarker -PathType Leaf) -or
        (Test-Path -LiteralPath $legacyInstalledExecutable -PathType Leaf)) {
        Remove-Item -LiteralPath $legacyInstallDirectory -Recurse -Force
    }
    else {
        Write-Warning "旧安装目录来源无法确认，已保留：$legacyInstallDirectory"
    }
}

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $installedExecutable
$shortcut.WorkingDirectory = $installDirectory
$shortcut.IconLocation = "$installedExecutable,0"
$shortcut.Description = '远程助手'
$shortcut.Save()

if (-not $NoLaunch) {
    Start-Process -FilePath $installedExecutable -WorkingDirectory $installDirectory
}

Write-Host "固定构建目录：$buildDirectory"
Write-Host "固定安装目录：$installDirectory"
Write-Host "桌面快捷方式：$shortcutPath"
