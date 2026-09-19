using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.Win32;

// Instalator: Steam Mic Auto (przelacza "Record Microphone" w Steam Game Recording razem z uruchomieniem gier).
// Argumenty: --uninstall | --silent | --dir <sciezka> | --files-only | --create-flag <plik> (wewnetrzny)
static class Program
{
    const string StartupFileName = "SteamMicAuto.vbs";
    const string LegacyStartupFileName = "mic-watcher-uruchom.vbs";
    const string FlagFileName = ".cef-enable-remote-debugging";
    const string WatcherScript = "mic-watcher.ps1";

    static bool silent;

    static readonly string DefaultGames =
        "# Lista procesow gier, przy ktorych ma sie wlaczac nagrywanie mikrofonu w Steam Game Recording.\r\n" +
        "# Jedna nazwa procesu na linie, BEZ .exe. Linie zaczynajace sie od # sa ignorowane.\r\n" +
        "#\r\n" +
        "# Jak znalezc nazwe procesu: uruchom gre -> Menedzer zadan (Ctrl+Shift+Esc) -> zakladka\r\n" +
        "# \"Szczegoly\" -> skopiuj nazwe z kolumny \"Nazwa\" bez .exe\r\n" +
        "# Zmiany sa wczytywane na biezaco - nie trzeba restartowac.\r\n" +
        "\r\n" +
        "Phasmophobia\r\n";

    static int Main(string[] args)
    {
        bool uninstall = false, filesOnly = false;
        string dirArg = null, createFlag = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            if (a == "--uninstall") uninstall = true;
            else if (a == "--silent") silent = true;
            else if (a == "--files-only") filesOnly = true;
            else if (a == "--dir" && i + 1 < args.Length) dirArg = args[++i];
            else if (a == "--create-flag" && i + 1 < args.Length) createFlag = args[++i];
        }

        if (createFlag != null)
        {
            try { File.WriteAllText(createFlag, ""); return 0; }
            catch { return 1; }
        }

        string installDir = dirArg != null
            ? Path.GetFullPath(dirArg)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamMicAuto");

        Console.Title = "Steam Mic Auto - " + (uninstall ? "odinstalowanie" : "instalacja");
        int code;
        try { code = uninstall ? Uninstall(installDir) : Install(installDir, filesOnly); }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("BLAD: " + ex.Message);
            code = 1;
        }
        Pause();
        return code;
    }

    static int Install(string installDir, bool filesOnly)
    {
        Console.WriteLine("=== Steam Mic Auto - instalacja ===");
        Console.WriteLine();

        string steamDir = FindSteamDir();
        if (steamDir == null)
        {
            Console.WriteLine("[X] Nie znaleziono Steama (steam.exe). Zainstaluj Steam i uruchom ten program ponownie.");
            return 1;
        }
        Console.WriteLine("[OK] Steam:            " + steamDir);
        Console.WriteLine("[OK] Folder instalacji: " + installDir);

        Directory.CreateDirectory(installDir);
        ExtractResource("payload.mic-watcher.ps1", Path.Combine(installDir, WatcherScript));
        ExtractResource("payload.steam-recording-mic.ps1", Path.Combine(installDir, "steam-recording-mic.ps1"));
        string gamesPath = Path.Combine(installDir, "games.txt");
        if (!File.Exists(gamesPath)) File.WriteAllText(gamesPath, DefaultGames, new UTF8Encoding(false));
        Console.WriteLine("[OK] Skrypty zapisane (lista gier: " + gamesPath + ")");

        string flagPath = Path.Combine(steamDir, FlagFileName);
        if (!EnsureFlag(flagPath))
        {
            Console.WriteLine("[X] Nie udalo sie utworzyc pliku " + flagPath);
            Console.WriteLine("    Utworz go recznie (pusty plik) i uruchom program ponownie.");
            return 1;
        }
        Console.WriteLine("[OK] Zdalne debugowanie Steama wlaczone (plik flagi)");

        if (filesOnly)
        {
            Console.WriteLine();
            Console.WriteLine("Tryb --files-only: pominieto autostart, zatrzymywanie starych procesow i uruchomienie.");
            return 0;
        }

        int killed = StopWatchers();
        if (killed > 0) Console.WriteLine("[OK] Zatrzymano poprzednie uruchomione watchery: " + killed);

        string startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        string legacy = Path.Combine(startupDir, LegacyStartupFileName);
        if (File.Exists(legacy) && File.ReadAllText(legacy).IndexOf(WatcherScript, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            File.Delete(legacy);
            Console.WriteLine("[OK] Usunieto stary wpis autostartu (" + LegacyStartupFileName + ") - zastapiony nowym");
        }

        string startupFile = Path.Combine(startupDir, StartupFileName);
        string ps1 = Path.Combine(installDir, WatcherScript);
        File.WriteAllText(startupFile,
            "Set sh = CreateObject(\"WScript.Shell\")\r\n" +
            "sh.Run \"powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"\"" + ps1 + "\"\"\", 0, False\r\n",
            Encoding.ASCII);
        Console.WriteLine("[OK] Autostart dodany: " + startupFile);

        Process.Start(new ProcessStartInfo("wscript.exe", "\"" + startupFile + "\"") { UseShellExecute = false });
        Console.WriteLine("[OK] Watcher uruchomiony w tle");

        Console.WriteLine();
        EnsureSteamDebugPort(steamDir);

        Console.WriteLine();
        Console.WriteLine("Gotowe. Sprawdz liste gier: " + gamesPath);
        if (Ask("Otworzyc liste gier w Notatniku?", true))
            Process.Start(new ProcessStartInfo("notepad.exe", "\"" + gamesPath + "\"") { UseShellExecute = false });
        return 0;
    }

    static int Uninstall(string installDir)
    {
        Console.WriteLine("=== Steam Mic Auto - odinstalowanie ===");
        Console.WriteLine();

        int killed = StopWatchers();
        Console.WriteLine("[OK] Zatrzymano watchery: " + killed);

        string startupFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupFileName);
        if (File.Exists(startupFile)) { File.Delete(startupFile); Console.WriteLine("[OK] Usunieto autostart"); }

        if (Directory.Exists(installDir))
        {
            try { Directory.Delete(installDir, true); Console.WriteLine("[OK] Usunieto folder " + installDir); }
            catch (Exception ex) { Console.WriteLine("[!] Nie udalo sie usunac " + installDir + ": " + ex.Message); }
        }

        string steamDir = FindSteamDir();
        if (steamDir != null)
        {
            string flagPath = Path.Combine(steamDir, FlagFileName);
            if (File.Exists(flagPath) && Ask("Wylaczyc tez zdalne debugowanie Steama (usunac plik flagi)?", true))
            {
                try { File.Delete(flagPath); Console.WriteLine("[OK] Usunieto plik flagi (zadziala po restarcie Steama)"); }
                catch (UnauthorizedAccessException)
                {
                    Console.WriteLine("[!] Brak uprawnien - usun recznie: " + flagPath);
                }
            }
        }
        Console.WriteLine();
        Console.WriteLine("Uwaga: przelacznik Record Microphone w Steamie zostaje w ostatnio ustawionym stanie.");
        return 0;
    }

    static bool EnsureFlag(string flagPath)
    {
        if (File.Exists(flagPath)) return true;
        try { File.WriteAllText(flagPath, ""); return true; }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        Console.WriteLine("[..] Folder Steama wymaga uprawnien administratora - potwierdz okno UAC");
        try
        {
            var psi = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--create-flag \"" + flagPath + "\"");
            psi.Verb = "runas";
            psi.UseShellExecute = true;
            using (Process p = Process.Start(psi)) { p.WaitForExit(); }
        }
        catch { }
        return File.Exists(flagPath);
    }

    static void EnsureSteamDebugPort(string steamDir)
    {
        if (DebugPortUp())
        {
            Console.WriteLine("[OK] Steam odpowiada na porcie debugowania - wszystko dziala.");
            return;
        }

        Process[] steam = Process.GetProcessesByName("steam");
        if (steam.Length == 0)
        {
            Console.WriteLine("[i] Steam nie jest uruchomiony. Uruchom go - port debugowania wlaczy sie automatycznie.");
            return;
        }

        Console.WriteLine("[!] Steam dziala, ale zostal uruchomiony zanim wlaczono debugowanie - wymaga restartu.");
        if (!Ask("Zrestartowac Steam teraz? (zamknie tez uruchomione gry i overlay)", false))
        {
            Console.WriteLine("[i] Zrestartuj Steam recznie (Steam -> Wyjdz, potem uruchom ponownie).");
            return;
        }

        string steamExe = Path.Combine(steamDir, "steam.exe");
        Process.Start(new ProcessStartInfo(steamExe, "-shutdown") { UseShellExecute = false });
        Console.Write("[..] Zamykanie Steama");
        for (int i = 0; i < 90 && Process.GetProcessesByName("steam").Length > 0; i++) { Thread.Sleep(500); Console.Write("."); }
        Console.WriteLine();
        Process.Start(new ProcessStartInfo(steamExe) { UseShellExecute = false });
        Console.Write("[..] Uruchamianie Steama");
        for (int i = 0; i < 120 && !DebugPortUp(); i++) { Thread.Sleep(1000); Console.Write("."); }
        Console.WriteLine();
        Console.WriteLine(DebugPortUp()
            ? "[OK] Steam odpowiada na porcie debugowania - wszystko dziala."
            : "[!] Steam jeszcze nie odpowiada na porcie 8080 - poczekaj az w pelni sie uruchomi.");
    }

    static bool DebugPortUp()
    {
        try
        {
            var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:8080/json");
            req.Timeout = 2000;
            req.Proxy = null;
            using (var resp = (HttpWebResponse)req.GetResponse()) return resp.StatusCode == HttpStatusCode.OK;
        }
        catch { return false; }
    }

    static int StopWatchers()
    {
        int killed = 0;
        try
        {
            using (var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='powershell.exe'"))
            {
                foreach (System.Management.ManagementBaseObject mo in searcher.Get())
                {
                    string cl = mo["CommandLine"] as string;
                    if (cl == null || cl.IndexOf(WatcherScript, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    try { Process.GetProcessById(Convert.ToInt32(mo["ProcessId"])).Kill(); killed++; }
                    catch { }
                }
            }
        }
        catch { }
        if (killed > 0) Thread.Sleep(600);
        return killed;
    }

    static string FindSteamDir()
    {
        var candidates = new List<string>();
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                if (k != null && k.GetValue("SteamPath") is string) candidates.Add((string)k.GetValue("SteamPath"));
        }
        catch { }
        try
        {
            using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                if (k != null && k.GetValue("InstallPath") is string) candidates.Add((string)k.GetValue("InstallPath"));
        }
        catch { }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));

        foreach (string c in candidates)
        {
            try
            {
                string full = Path.GetFullPath(c.Replace('/', '\\'));
                if (File.Exists(Path.Combine(full, "steam.exe"))) return full;
            }
            catch { }
        }
        return null;
    }

    static void ExtractResource(string name, string destination)
    {
        using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
        {
            if (s == null) throw new InvalidOperationException("Brak zasobu w setup.exe: " + name);
            using (FileStream f = File.Create(destination)) s.CopyTo(f);
        }
    }

    static bool Ask(string question, bool defaultYes)
    {
        if (silent || Console.IsInputRedirected) return false;
        Console.Write(question + (defaultYes ? " [T/n] " : " [t/N] "));
        ConsoleKeyInfo key = Console.ReadKey();
        Console.WriteLine();
        if (key.Key == ConsoleKey.Enter) return defaultYes;
        return key.KeyChar == 't' || key.KeyChar == 'T' || key.KeyChar == 'y' || key.KeyChar == 'Y';
    }

    static void Pause()
    {
        if (silent || Console.IsInputRedirected) return;
        Console.WriteLine();
        Console.Write("Nacisnij dowolny klawisz, aby zamknac...");
        Console.ReadKey(true);
    }
}
