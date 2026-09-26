[CmdletBinding()]
param(
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'

$projectPath = Join-Path $PSScriptRoot 'LunaDesktopHelper.csproj'
$buildDirectory = Join-Path $PSScriptRoot '.build\windows-release'
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\LunaDesktopHelper'
$installedExecutable = Join-Path $installDirectory 'LunaDesktopHelper.exe'
$installMarker = Join-Path $installDirectory '.luna-desktop-helper-install'
$desktopDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
$shortcutPath = Join-Path $desktopDirectory '远程助手.lnk'

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "找不到项目文件：$projectPath"
}

New-Item -ItemType Directory -Path $buildDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null

$expectedInstallDirectory = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\LunaDesktopHelper'))
$actualInstallDirectory = [IO.Path]::GetFullPath($installDirectory)
if (-not $actualInstallDirectory.Equals($expectedInstallDirectory, [StringComparison]::OrdinalIgnoreCase)) {
    throw "拒绝更新意外的安装目录：$actualInstallDirectory"
}

dotnet publish $projectPath `
    --configuration Release `
    --output $buildDirectory `
    --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Windows Release 发布失败，退出代码：$LASTEXITCODE"
}

$projectBuildRoot = Join-Path $PSScriptRoot '.build'
$replaceableProcesses = Get-Process -Name 'LunaDesktopHelper' -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Path -eq $installedExecutable -or
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
