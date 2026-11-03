$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$watcher = Join-Path $root 'gpu_comfy_watch.ps1'
$controllerExe = Join-Path $root 'HaloGuard.exe'
$controlRoot = Join-Path $env:LOCALAPPDATA 'ComfyUiGpuWatch'
$startup = [Environment]::GetFolderPath('Startup')
$desktop = [Environment]::GetFolderPath('Desktop')
$hostExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$shell = New-Object -ComObject WScript.Shell

function Set-Shortcut([string]$Path, [string]$ScriptPath, [string]$Description) {
    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $hostExe
    $shortcut.Arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -File "' + $ScriptPath + '"'
    $shortcut.WorkingDirectory = $root
    $shortcut.WindowStyle = 1
    $shortcut.Description = $Description
    $shortcut.Save()
}

$startupShortcut = $shell.CreateShortcut((Join-Path $startup '光环守护 · 看门狗.lnk'))
$startupShortcut.TargetPath = $hostExe
$startupShortcut.Arguments = '-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $watcher + '"'
$startupShortcut.WorkingDirectory = $root
$startupShortcut.WindowStyle = 7
$startupShortcut.Description = 'Silently monitor NVIDIA GPU and ComfyUI at user logon'
$startupShortcut.Save()
$desktopShortcut = $shell.CreateShortcut((Join-Path $desktop '光环守护.lnk'))
$desktopShortcut.TargetPath = $controllerExe
$desktopShortcut.WorkingDirectory = $root
$desktopShortcut.WindowStyle = 1
$desktopShortcut.Description = '光环守护 - 打开本机 ComfyUI 与 NVIDIA 监控面板'
Remove-Item -LiteralPath (Join-Path $desktop 'ComfyUI GPU Monitor.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $startup 'ComfyUI GPU Watchdog.lnk') -Force -ErrorAction SilentlyContinue
$desktopShortcut.Save()
$null = New-Item -ItemType Directory -Path $controlRoot -Force
Remove-Item -LiteralPath (Join-Path $controlRoot 'disabled.flag'),(Join-Path $controlRoot 'stop.flag'),(Join-Path $controlRoot 'pause.flag') -Force -ErrorAction SilentlyContinue
if (-not (Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^powershell(\.exe)?$' -and $_.CommandLine -like "*$watcher*" } | Select-Object -First 1)) {
    $arguments = '-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $watcher + '"'
    Start-Process -FilePath $hostExe -WindowStyle Hidden -ArgumentList $arguments
}
