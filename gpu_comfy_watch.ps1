# Local GPU / ComfyUI watchdog. Runs silently in the logged-in user's session.
# It only reboots after persistent GPU/display failure evidence and failed ComfyUI recovery.
$ErrorActionPreference = 'SilentlyContinue'

$ComfyRoot = if ($env:COMFYUI_ROOT) { $env:COMFYUI_ROOT } else { 'D:\ComfyUI\ComfyUI' }
$LogFiles = @(
    (Join-Path $ComfyRoot 'user\comfyui_8188.log'),
    (Join-Path $ComfyRoot 'logs\comfyui.log')
)
$MonitorLog = Join-Path $ComfyRoot 'logs\gpu_comfy_watch.log'
$ControlRoot = Join-Path $env:LOCALAPPDATA 'ComfyUiGpuWatch'
$PauseFile = Join-Path $ControlRoot 'pause.flag'
$StopFile = Join-Path $ControlRoot 'stop.flag'
$DisabledFile = Join-Path $ControlRoot 'disabled.flag'
$null = New-Item -ItemType Directory -Path $ControlRoot -Force
$GpuErrorPattern = '(?i)CUDA device lost|CUDA_ERROR_UNKNOWN|sticky error|sticky CUDA|device has been lost|GPU device.*(lost|removed)|NVIDIA.*(driver reset|device lost)|nvlddmkm.*(reset|recover|timeout)'
$PollSeconds = 60
$RecoveryWaitMinutes = 3
$EvidenceFreshMinutes = 15

function Write-WatchLog([string]$Message) {
    $stamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
    Add-Content -LiteralPath $MonitorLog -Value "[$stamp] $Message" -Encoding UTF8
}

function Get-ComfyState {
    $proc = Get-CimInstance Win32_Process |
        Where-Object { $_.Name -match '^python(w)?\.exe$' -and $_.CommandLine -match '(?i)ComfyUI[\\/]+main\.py' } |
        Select-Object -First 1
    $listening = $false
    foreach ($port in @(8188, 8189)) {
        if (Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction SilentlyContinue | Select-Object -First 1) {
            $listening = $true
            break
        }
    }
    $healthy = $false
    foreach ($url in @('http://127.0.0.1:8188/system_stats', 'http://127.0.0.1:8189/system_stats')) {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri $url -TimeoutSec 3
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 500) {
                $healthy = $true
                break
            }
        } catch {}
    }
    [pscustomobject]@{
        Active = [bool]($proc -or $listening)
        Healthy = $healthy
        ProcessId = if ($proc) { $proc.ProcessId } else { $null }
    }
}

function Get-GpuProbe {
    try {
        $cmd = Get-Command 'nvidia-smi.exe' -ErrorAction Stop | Select-Object -First 1
        $exe = if ($cmd.Path) { $cmd.Path } else { $cmd.Source }
        $output = & $exe -L 2>&1
        $exitCode = $LASTEXITCODE
        $ok = ($exitCode -eq 0) -and (($output | Out-String) -match '(?i)GPU\s+\d+:')
        [pscustomobject]@{ Ok = [bool]$ok; Output = (($output | Out-String).Trim()) }
    } catch {
        [pscustomobject]@{ Ok = $false; Output = $_.Exception.Message }
    }
}

function Get-FreshLogEvidence {
    $hits = @()
    $cutoff = (Get-Date).AddMinutes(-$EvidenceFreshMinutes)
    foreach ($path in $LogFiles) {
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $item = Get-Item -LiteralPath $path
        if ($item.LastWriteTime -lt $cutoff) { continue }
        foreach ($line in (Get-Content -LiteralPath $path -Tail 300)) {
            if ($line -match $GpuErrorPattern) {
                # ComfyUI log lines carry their own timestamp. Ignore stale matching
                # lines even when later ordinary messages keep the same file active.
                if ($line -match '^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})') {
                    $lineTime = [datetime]::MinValue
                    if ([datetime]::TryParse($Matches[1], [ref]$lineTime) -and $lineTime -lt $cutoff) { continue }
                } elseif ($item.LastWriteTime -lt (Get-Date).AddMinutes(-2)) {
                    continue
                }
                $hits += "[$($item.Name)] $line"
            }
        }
    }
    return @($hits | Select-Object -Last 8)
}

function Get-FreshDriverEvidence {
    $cutoff = (Get-Date).AddMinutes(-$EvidenceFreshMinutes)
    $hits = @()
    try {
        $events = Get-WinEvent -FilterHashtable @{ LogName = 'System'; StartTime = $cutoff } -MaxEvents 120
        foreach ($event in $events) {
            $provider = [string]$event.ProviderName
            $message = [string]$event.Message
            if (($provider -match '(?i)Display|nvlddmkm|NVIDIA') -and
                (($event.Id -eq 4101) -or ($message -match '(?i)display driver.*(stopped responding|recovered|reset)|nvlddmkm.*(reset|timeout|recover)'))) {
                $hits += "[$($event.TimeCreated.ToString('yyyy-MM-dd HH:mm:ss'))][$provider][$($event.Id)] $message"
            }
        }
    } catch {}
    return @($hits | Select-Object -First 5)
}

# One instance per interactive user.
$mutex = [Threading.Mutex]::new($false, 'Local\ComfyUiGpuWatch')
if (-not $mutex.WaitOne(0)) { exit 0 }
if (Test-Path -LiteralPath $DisabledFile) {
    Write-WatchLog 'Watchdog is disabled; exiting.'
    exit 0
}

$gpuFailCount = 0
$previousComfy = Get-ComfyState
$candidateSince = $null
$candidateReason = @()
$candidateWasComfyActive = $false
$restartIssuedThisSession = $false
$isPaused = $false

Write-WatchLog 'Watchdog started.'
while ($true) {
    if (Test-Path -LiteralPath $DisabledFile) {
        Write-WatchLog 'Watchdog disabled from the local control panel.'
        Remove-Item -LiteralPath $StopFile -Force
        exit 0
    }
    if (Test-Path -LiteralPath $StopFile) {
        Write-WatchLog 'Watchdog stopped from the local control panel.'
        Remove-Item -LiteralPath $StopFile -Force
        exit 0
    }
    if (Test-Path -LiteralPath $PauseFile) {
        if (-not $isPaused) { Write-WatchLog 'Watchdog paused from the local control panel.' }
        $isPaused = $true
        Start-Sleep -Seconds 2
        continue
    } elseif ($isPaused) {
        Write-WatchLog 'Watchdog resumed from the local control panel.'
        $isPaused = $false
        $gpuFailCount = 0
        $candidateSince = $null
        $candidateReason = @()
        $candidateWasComfyActive = $false
        $previousComfy = Get-ComfyState
    }

    $gpu = Get-GpuProbe
    if ($gpu.Ok) { $gpuFailCount = 0 } else { $gpuFailCount++ }

    $comfy = Get-ComfyState
    $logEvidence = Get-FreshLogEvidence
    $driverEvidence = Get-FreshDriverEvidence

    $hasEvidence = ($gpuFailCount -ge 2) -or ($logEvidence.Count -gt 0) -or ($driverEvidence.Count -gt 0)
    if ($hasEvidence) {
        if (-not $candidateSince) {
            $candidateSince = Get-Date
            $candidateWasComfyActive = $previousComfy.Active -or $comfy.Active
            $candidateReason = @()
            if ($gpuFailCount -ge 2) { $candidateReason += "nvidia-smi failed on $gpuFailCount consecutive checks: $($gpu.Output)" }
            if ($logEvidence.Count -gt 0) { $candidateReason += $logEvidence }
            if ($driverEvidence.Count -gt 0) { $candidateReason += $driverEvidence }
            Write-WatchLog ("GPU fault candidate detected. ComfyUI active at detection: $candidateWasComfyActive. " + ($candidateReason -join ' | '))
        } else {
            if ($gpuFailCount -ge 2 -and -not ($candidateReason -match 'nvidia-smi failed')) {
                $candidateReason += "nvidia-smi failure persists: $($gpu.Output)"
            }
            if ($logEvidence.Count -gt 0) { $candidateReason += $logEvidence }
            if ($driverEvidence.Count -gt 0) { $candidateReason += $driverEvidence }
        }

        $ageMinutes = ((Get-Date) - $candidateSince).TotalMinutes
        $hasCudaStickyEvidence = [bool]($logEvidence | Where-Object { $_ -match '(?i)CUDA device lost|CUDA_ERROR_UNKNOWN|sticky error|sticky CUDA|device has been lost' })
        if ($comfy.Healthy -and $gpu.Ok -and -not $hasCudaStickyEvidence) {
            # A healthy endpoint plus a visible GPU means a driver reset recovered.
            $comfyRecovered = $true
        } else {
            # A responsive web UI alone does not prove its CUDA device recovered.
            $comfyRecovered = $false
        }

        if ($comfyRecovered) {
            Write-WatchLog 'ComfyUI endpoint recovered; cleared pending reboot condition.'
            $candidateSince = $null
            $candidateReason = @()
            $candidateWasComfyActive = $false
        } elseif ($candidateWasComfyActive -and $ageMinutes -ge $RecoveryWaitMinutes -and -not $restartIssuedThisSession) {
            # Persistent device-loss evidence plus a ComfyUI instance that was active at fault onset
            # and did not recover during the grace period.
            $stamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
            Write-WatchLog "CONFIRMED GPU FAILURE; ComfyUI did not recover after $([math]::Round($ageMinutes,1)) minutes. Reboot scheduled. Evidence: $($candidateReason -join ' | ')"
            $restartIssuedThisSession = $true
            try {
                & "$env:SystemRoot\System32\shutdown.exe" /r /t 30 /c "Persistent NVIDIA GPU failure; ComfyUI did not recover. See $MonitorLog"
                if ($LASTEXITCODE -eq 0) {
                    Write-WatchLog 'Windows restart scheduled in 30 seconds.'
                    Start-Sleep -Seconds 35
                    exit 0
                } else {
                    Write-WatchLog "shutdown.exe returned exit code $LASTEXITCODE. No automatic retry this session."
                }
            } catch {
                Write-WatchLog "Unable to schedule Windows restart: $($_.Exception.Message). No automatic retry this session."
            }
        }
    } else {
        if ($candidateSince) {
            Write-WatchLog 'GPU probe/log evidence cleared; canceled pending recovery timer.'
        }
        $candidateSince = $null
        $candidateReason = @()
        $candidateWasComfyActive = $false
    }

    $previousComfy = $comfy
    for ($waited = 0; $waited -lt $PollSeconds; $waited += 2) {
        if ((Test-Path -LiteralPath $StopFile) -or (Test-Path -LiteralPath $PauseFile)) { break }
        Start-Sleep -Seconds 2
    }
}

