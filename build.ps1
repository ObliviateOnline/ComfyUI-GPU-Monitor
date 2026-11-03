$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'gpu_comfy_watch_panel.cs'
$output = Join-Path $PSScriptRoot 'HaloGuard.exe'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The .NET Framework C# compiler (csc.exe) was not found.'
}

# WebView2 SDK + the app icon - must sit next to this script.
$webviewCore = Join-Path $PSScriptRoot 'Microsoft.Web.WebView2.Core.dll'
$webviewForms = Join-Path $PSScriptRoot 'Microsoft.Web.WebView2.WinForms.dll'
$webviewLoader = Join-Path $PSScriptRoot 'WebView2Loader.dll'
$appIcon = Join-Path $PSScriptRoot 'app.ico'
foreach ($f in @($webviewCore, $webviewForms, $webviewLoader, $appIcon)) {
    if (-not (Test-Path -LiteralPath $f)) {
        throw ('Missing file: ' + $f + ' - the HALOFLUX panel needs it to build.')
    }
}

# Keep a UTF-8 BOM so Windows PowerShell and the Framework compiler read Chinese UI labels correctly.
$text = [System.IO.File]::ReadAllText($source, [System.Text.Encoding]::UTF8)
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText($source, $text, $utf8Bom)

# /platform:x64 because WebView2Loader.dll here is the 64-bit build
# /win32icon embeds app.ico as the exe icon (Explorer, taskbar, shortcut)
& $compiler /nologo /target:winexe /platform:x64 ('/out:' + $output) ('/win32icon:' + $appIcon) `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll `
    ('/reference:' + $webviewCore) ('/reference:' + $webviewForms) $source
if ($LASTEXITCODE -ne 0) {
    throw 'C# compilation failed.'
}
Write-Output $output
