# Controls the "Record Microphone" toggle (Steam -> Settings -> Game Recording)
# through Steam's embedded browser (Chrome DevTools Protocol, 127.0.0.1:8080).
# Requires the file ".cef-enable-remote-debugging" in the Steam folder and a Steam restart.
# This file is dot-sourced by mic-watcher.ps1.

Add-Type -Namespace Win32 -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
'@ -ErrorAction SilentlyContinue

function Invoke-SteamCdp([string]$Title, [string]$Expression) {
    $targets = Invoke-RestMethod -Uri 'http://127.0.0.1:8080/json' -TimeoutSec 3
    $page = $targets | Where-Object { $_.title -eq $Title } | Select-Object -First 1
    if (-not $page) { return $null }

    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $ct = [Threading.CancellationToken]::None
    try {
        $ws.ConnectAsync([Uri]$page.webSocketDebuggerUrl, $ct).Wait()
        $msg = @{ id = 1; method = 'Runtime.evaluate'; params = @{ expression = $Expression; returnByValue = $true; awaitPromise = $true } } | ConvertTo-Json -Depth 6 -Compress
        $bytes = [Text.Encoding]::UTF8.GetBytes($msg)
        $ws.SendAsync([ArraySegment[byte]]::new($bytes), 'Text', $true, $ct).Wait()

        $buf = New-Object byte[] 65536
        $sb = New-Object Text.StringBuilder
        do {
            $res = $ws.ReceiveAsync([ArraySegment[byte]]::new($buf), $ct).Result
            [void]$sb.Append([Text.Encoding]::UTF8.GetString($buf, 0, $res.Count))
        } while (-not $res.EndOfMessage)
        $out = $sb.ToString() | ConvertFrom-Json
        return $out.result.result.value
    }
    finally {
        if ($ws.State -eq 'Open') { $ws.CloseAsync('NormalClosure', '', $ct).Wait() }
        $ws.Dispose()
    }
}

$script:SetMicJs = @'
(async function(want){
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  let tab = null;
  for (let i = 0; i < 30 && !tab; i++) {
    tab = [...document.querySelectorAll('[role=tab]')].find(t => t.textContent.trim() === 'Game Recording');
    if (!tab) await sleep(200);
  }
  if (!tab) return 'no-tab';
  tab.click();
  const findCb = () => {
    const lbl = [...document.querySelectorAll('div')].find(e => e.children.length === 0 && /^Record Microphone$/i.test(e.textContent.trim()));
    return lbl ? lbl.parentElement.parentElement.parentElement.querySelector('[role=checkbox]') : null;
  };
  let cb = null;
  for (let i = 0; i < 30 && !cb; i++) { cb = findCb(); if (!cb) await sleep(200); }
  if (!cb) return 'no-checkbox';
  if ((cb.getAttribute('aria-checked') === 'true') !== want) { cb.click(); await sleep(600); }
  return 'checked=' + findCb().getAttribute('aria-checked');
})(__WANT__)
'@

# Returns $true if the toggle has the requested state afterwards, $false on failure (e.g. Steam not running).
function Set-SteamRecordMic([bool]$Enable) {
    try {
        $wasOpen = [bool](Invoke-SteamCdp 'Steam Settings' '1')
        if (-not $wasOpen) {
            [void](Invoke-SteamCdp 'SharedJSContext' "SteamClient.URL.ExecuteSteamURL('steam://open/settings'); 1")
            for ($i = 0; $i -lt 25 -and -not (Invoke-SteamCdp 'Steam Settings' '1'); $i++) { Start-Sleep -Milliseconds 200 }
            $proc = Get-Process -Name steamwebhelper -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -eq 'Steam Settings' } | Select-Object -First 1
            if ($proc) { [void][Win32.Native]::ShowWindow($proc.MainWindowHandle, 7) }
        }

        $js = $script:SetMicJs.Replace('__WANT__', $Enable.ToString().ToLower())
        $result = Invoke-SteamCdp 'Steam Settings' $js

        if (-not $wasOpen) {
            [void](Invoke-SteamCdp 'Steam Settings' 'setTimeout(() => window.close(), 50); 1')
            for ($i = 0; $i -lt 15 -and (Invoke-SteamCdp 'Steam Settings' '1'); $i++) { Start-Sleep -Milliseconds 200 }
        }

        return ($result -eq ('checked=' + $Enable.ToString().ToLower()))
    }
    catch {
        return $false
    }
}
