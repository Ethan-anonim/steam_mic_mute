# Wlacza "Record Microphone" w Steam Game Recording, gdy dziala gra z games.txt,
# i wylacza go, gdy zadnej z nich nie ma. Nie rusza mikrofonu w Windows ani Discorda.
# Dziala w petli w tle (autostart przez folder Uruchamianie - mic-watcher-uruchom.vbs).

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

Write-Log "=== mic-watcher wystartowal (sterowanie: Steam Record Microphone) ==="

$applied = $null      # ostatni stan, ktory udalo sie ustawic w Steamie
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
            if ($desired) { Write-Log "Wykryto gre '$matchedName' -> Record Microphone: WLACZONE" }
            else          { Write-Log "Brak gry z listy -> Record Microphone: WYLACZONE" }
        }
        elseif (-not $failLogged) {
            Write-Log "Nie udalo sie ustawic Record Microphone (Steam nie dziala lub port 8080 wylaczony) - ponawiam"
            $failLogged = $true
        }
        if ($desired -ne $applied) { Start-Sleep -Seconds 12 }
    }

    Start-Sleep -Seconds 3
}
