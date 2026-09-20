# Steam Mic Auto

**English** | [Polski](README.pl.md)

Automatically turns **Record Microphone** in Steam Game Recording on and off, depending on whether one of your games is running (e.g. Phasmophobia, co-op games).

Steam only has one global toggle for recording the microphone (Settings → Game Recording → Audio Recording): no per-game setting and no hotkey. This tool flips it for you, so your microphone ends up in clips only during the games on your list. It does not touch the Windows microphone or other programs (Discord, Voicemod, etc.).

## Requirements

- Windows 10/11 (PowerShell 5.1 is built in)
- Steam with Game Recording enabled
- **Steam set to English** (the script recognizes the "Game Recording" tab and the "Record Microphone" toggle by their English names)

## Installation

1. Download `setup.exe` from [Releases](../../releases) (or `setup/setup.exe`) and run it.
2. The installer automatically:
   - finds Steam,
   - enables remote debugging in it (an empty `.cef-enable-remote-debugging` file in the Steam folder; a UAC prompt appears if admin rights are needed),
   - installs the scripts to `%LOCALAPPDATA%\SteamMicAuto`,
   - adds autostart (Startup folder) and starts the program in the background,
   - offers to restart Steam if it was running without debugging enabled (it will not work without a restart).
3. Choose where to keep your game list (`games.txt`): Documents, Desktop, the install folder, or any custom folder. The program adapts to your choice, and a list from a previous install is moved there automatically.
4. Add your games to the list (the installer offers to open it).

### Game list

One process name per line, **without `.exe`**. Changes are picked up automatically.

To find a process name: start the game → Task Manager (Ctrl+Shift+Esc) → "Details" tab → "Name" column.

```
Phasmophobia
```

### Uninstall

```
%LOCALAPPDATA%\SteamMicAuto\uninstall.exe
```

The installer adds Steam Mic Auto to **Windows Settings → Apps**, so you can also uninstall it from there (or run `setup.exe --uninstall`). The uninstaller stops the program and removes everything related to it: autostart, the entry in Apps, the install folder (including the uninstaller itself), and, when you agree, your game list and the Steam remote debugging flag file. The Record Microphone toggle stays in its last state.

Use `uninstall.exe --purge` to remove absolutely everything without questions (including the game list and the flag file). With `--silent` and no `--purge`, a game list outside the install folder and the flag file are left in place.

Other arguments: `--silent` (no prompts), `--dir <path>` (different install folder), `--games-dir <folder or .txt file>` (game list location, skips the question), `--files-only` (only extract files, no autostart or launch).

## How it works

1. A PowerShell program (`mic-watcher.ps1`) checks every 3 seconds whether a process from `games.txt` is running.
2. When the state changes, it uses the Chrome DevTools Protocol (`127.0.0.1:8080`) to briefly open Steam's Settings window, go to Game Recording, flip "Record Microphone", and close the window.
3. If Steam is not running, it retries after a few seconds.

Log: `%LOCALAPPDATA%\SteamMicAuto\mic-watcher.log`

## Notes and limitations

- **Security:** with remote debugging enabled, any program on your computer can control the Steam client through the local port 8080. The port is not reachable from the network, but if you are not comfortable with that, do not install this or uninstall it (that removes the flag file).
- When a game starts, the Steam Settings window may flash on top for 1-2 seconds.
- The program drives Steam's UI, so a major redesign of the Settings pages in a Steam update may break it.
- `setup.exe` is not code-signed, so SmartScreen or your antivirus may show a warning. The source is in `setup/src`, so you can build it yourself.

## Building from source

You only need Windows (it uses the C# compiler built into the OS):

```
powershell -ExecutionPolicy Bypass -File setup\src\build.ps1
```

This produces `setup\setup.exe` with the scripts from `setup\src\payload` embedded.

## Layout

```
setup/
  setup.exe            installer
  src/
    Setup.cs           installer source
    build.ps1          builds setup.exe
    payload/
      mic-watcher.ps1          game-detection loop
      steam-recording-mic.ps1  controls the toggle in Steam
```
