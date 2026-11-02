$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'gpu_comfy_watch_panel.cs'
$output = Join-Path $PSScriptRoot 'ComfyUIGpuMonitor.exe'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The .NET Framework C# compiler (csc.exe) was not found.'
}

# Keep a UTF-8 BOM so Windows PowerShell and the Framework compiler read Chinese UI labels correctly.
$text = [System.IO.File]::ReadAllText($source, [System.Text.Encoding]::UTF8)
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText($source, $text, $utf8Bom)

& $compiler /nologo /target:winexe (('/out:' + $output)) /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll $source
if ($LASTEXITCODE -ne 0) {
    throw 'C# compilation failed.'
}
Write-Output $output
