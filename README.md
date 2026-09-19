# Steam Mic Auto

Automatycznie włącza i wyłącza **Record Microphone** w Steam Game Recording, zależnie od tego, czy działa jedna z Twoich gier (np. Phasmophobia, gry kooperacyjne).

Steam ma tylko jeden globalny przełącznik nagrywania mikrofonu (Ustawienia → Game Recording → Audio Recording), bez ustawienia per gra i bez skrótu klawiszowego. To narzędzie przełącza go za Ciebie: mikrofon trafia do klipów tylko podczas gier z Twojej listy. Nie rusza mikrofonu w Windows ani innych programów (Discord, Voicemod itd.).

## Wymagania

- Windows 10/11 (PowerShell 5.1 jest wbudowany)
- Steam z włączonym Game Recording
- **Steam w języku angielskim** (skrypt rozpoznaje zakładkę "Game Recording" i przełącznik "Record Microphone" po angielskich nazwach)

## Instalacja

1. Pobierz `setup/setup.exe` (lub z zakładki Releases) i uruchom.
2. Instalator sam:
   - znajdzie Steama,
   - włączy w nim zdalne debugowanie (pusty plik `.cef-enable-remote-debugging` w folderze Steama; jeśli potrzeba uprawnień, pojawi się okno UAC),
   - zainstaluje skrypty do `%LOCALAPPDATA%\SteamMicAuto`,
   - doda autostart (folder Uruchamianie) i od razu uruchomi program w tle,
   - zapyta o restart Steama, jeśli działał bez włączonego debugowania (bez restartu nie będzie działać).
3. Dopisz swoje gry do `%LOCALAPPDATA%\SteamMicAuto\games.txt` (instalator proponuje otwarcie pliku).

### Lista gier

Jedna nazwa procesu na linię, **bez `.exe`**. Zmiany są wczytywane na bieżąco.

Nazwę procesu znajdziesz tak: uruchom grę → Menedżer zadań (Ctrl+Shift+Esc) → zakładka "Szczegóły" → kolumna "Nazwa".

```
Phasmophobia
```

### Odinstalowanie

```
setup.exe --uninstall
```

Zatrzymuje program, usuwa autostart i pliki, a na pytanie może też wyłączyć zdalne debugowanie Steama (przełącznik Record Microphone zostaje w ostatnim stanie).

Inne argumenty: `--silent` (bez pytań), `--dir <ścieżka>` (inny folder instalacji), `--files-only` (tylko wypakuj pliki, bez autostartu i uruchamiania).

## Jak to działa

1. Program w PowerShellu (`mic-watcher.ps1`) co 3 sekundy sprawdza, czy działa proces z `games.txt`.
2. Gdy stan się zmienia, przez Chrome DevTools Protocol (`127.0.0.1:8080`) otwiera na chwilę okno Ustawień Steama, przechodzi do Game Recording, przełącza "Record Microphone" i zamyka okno.
3. Jeśli Steam nie działa, ponawia próbę po kilku sekundach.

Log: `%LOCALAPPDATA%\SteamMicAuto\mic-watcher.log`

## Uwagi i ograniczenia

- **Bezpieczeństwo:** włączone zdalne debugowanie oznacza, że każdy program na Twoim komputerze może sterować klientem Steam przez lokalny port 8080. Port nie jest dostępny z sieci, ale jeśli Ci to nie odpowiada, nie instaluj albo odinstaluj (usuwa plik flagi).
- W momencie startu gry okno Ustawień Steama może na 1-2 sekundy pojawić się na wierzchu.
- Program steruje interfejsem Steama, więc duża zmiana wyglądu Ustawień w aktualizacji Steama może go zepsuć.
- `setup.exe` nie jest podpisany cyfrowo, więc SmartScreen lub antywirus mogą wyświetlić ostrzeżenie. Kod źródłowy jest w `setup/src`, możesz zbudować go sam.

## Budowanie ze źródeł

Potrzebny jest tylko Windows (używa kompilatora C# wbudowanego w system):

```
powershell -ExecutionPolicy Bypass -File setup\src\build.ps1
```

Powstaje `setup\setup.exe` z osadzonymi skryptami z `setup\src\payload`.

## Struktura

```
setup/
  setup.exe            instalator
  src/
    Setup.cs           kod instalatora
    build.ps1          budowanie setup.exe
    payload/
      mic-watcher.ps1          pętla wykrywająca gry
      steam-recording-mic.ps1  sterowanie przełącznikiem w Steamie
```
