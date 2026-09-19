# Builds ..\setup.exe from Setup.cs and the files in payload\ (uses the C# compiler built into Windows).
$src = $PSScriptRoot
$out = Join-Path (Split-Path $src -Parent) 'setup.exe'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

& $csc /nologo /target:exe /platform:anycpu /optimize+ /out:$out `
    /r:System.Management.dll `
    "/resource:$src\payload\mic-watcher.ps1,payload.mic-watcher.ps1" `
    "/resource:$src\payload\steam-recording-mic.ps1,payload.steam-recording-mic.ps1" `
    "$src\Setup.cs"

if ($LASTEXITCODE -eq 0) { Write-Host "OK: $out" } else { Write-Host "Build failed" ; exit 1 }
