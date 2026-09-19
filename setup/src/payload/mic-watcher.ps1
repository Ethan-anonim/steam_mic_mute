# Turns "Record Microphone" in Steam Game Recording ON while a game from games.txt is running
# and OFF when none is. Does not touch the Windows microphone or Discord.
# Runs in a loop in the background (autostart via the Startup folder, see setup.exe).

$ErrorActionPreference = 'SilentlyContinue'

$singleInstance = New-Object System.Threading.Mutex($false, 'Local\SteamMicAutoWatcher')
if (-not $singleInstance.WaitOne(0)) { exit }

$scriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$configPath = Join-Path $scriptDir 'games.txt'
$logPath    = Join-Path $scriptDir 'mic-watcher.log'

. (Join-Path $scriptDir 'steam-recording-mic.ps1')

function Write-Log([string]$message) {
    $line = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $message
    Add-Content -Path $logPath -Value $line
    if ((Get-Item $logPath -ErrorAction SilentlyContinue).Length -gt 1MB) {
        Set-Content -Path $logPath -Value (Get-Content $logPath -Tail 200)
    }
}

Write-Log "=== mic-watcher started (controlling: Steam Record Microphone) ==="

$applied = $null      # last state successfully applied in Steam
$failLogged = $false

while ($true) {
    if (Test-Path $configPath) {
        $targets = Get-Content -Path $configPath |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ -and -not $_.StartsWith('#') }
    } else {
        $targets = @()
    }

    $matchedName = $null
    foreach ($name in $targets) {
        if (Get-Process -Name $name -ErrorAction SilentlyContinue) { $matchedName = $name; break }
    }
    $desired = [bool]$matchedName

    if ($desired -ne $applied) {
        if (Set-SteamRecordMic $desired) {
            $applied = $desired
            $failLogged = $false
            if ($desired) { Write-Log "Detected game '$matchedName' -> Record Microphone: ON" }
            else          { Write-Log "No listed game running -> Record Microphone: OFF" }
        }
        elseif (-not $failLogged) {
            Write-Log "Could not set Record Microphone (Steam not running or debug port 8080 disabled) - retrying"
            $failLogged = $true
        }
        if ($desired -ne $applied) { Start-Sleep -Seconds 12 }
    }

    Start-Sleep -Seconds 3
}
