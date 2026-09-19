using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.Win32;

// Installer: Steam Mic Auto (toggles "Record Microphone" in Steam Game Recording while listed games run).
// Arguments: --uninstall | --silent | --dir <path> | --files-only | --create-flag <file> (internal)
static class Program
{
    const string StartupFileName = "SteamMicAuto.vbs";
    const string LegacyStartupFileName = "mic-watcher-uruchom.vbs";
    const string FlagFileName = ".cef-enable-remote-debugging";
    const string WatcherScript = "mic-watcher.ps1";

    static bool silent;

    static readonly string DefaultGames =
        "# Process names of games during which Steam Game Recording should record the microphone.\r\n" +
        "# One process name per line, WITHOUT .exe. Lines starting with # are ignored.\r\n" +
        "#\r\n" +
        "# How to find a process name: start the game -> Task Manager (Ctrl+Shift+Esc) -> \"Details\" tab\r\n" +
        "# -> copy the value from the \"Name\" column without .exe\r\n" +
        "# Changes are picked up automatically - no restart needed.\r\n" +
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

        Console.Title = "Steam Mic Auto - " + (uninstall ? "uninstall" : "setup");
        int code;
        try { code = uninstall ? Uninstall(installDir) : Install(installDir, filesOnly); }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("ERROR: " + ex.Message);
            code = 1;
        }
        Pause();
        return code;
    }

    static int Install(string installDir, bool filesOnly)
    {
        Console.WriteLine("=== Steam Mic Auto - setup ===");
        Console.WriteLine();

        string steamDir = FindSteamDir();
        if (steamDir == null)
        {
            Console.WriteLine("[X] Steam (steam.exe) not found. Install Steam and run this program again.");
            return 1;
        }
        Console.WriteLine("[OK] Steam:          " + steamDir);
        Console.WriteLine("[OK] Install folder: " + installDir);

        Directory.CreateDirectory(installDir);
        ExtractResource("payload.mic-watcher.ps1", Path.Combine(installDir, WatcherScript));
        ExtractResource("payload.steam-recording-mic.ps1", Path.Combine(installDir, "steam-recording-mic.ps1"));
        string gamesPath = Path.Combine(installDir, "games.txt");
        if (!File.Exists(gamesPath)) File.WriteAllText(gamesPath, DefaultGames, new UTF8Encoding(false));
        Console.WriteLine("[OK] Scripts installed (game list: " + gamesPath + ")");

        string flagPath = Path.Combine(steamDir, FlagFileName);
        if (!EnsureFlag(flagPath))
        {
            Console.WriteLine("[X] Could not create " + flagPath);
            Console.WriteLine("    Create an empty file with that name manually and run this program again.");
            return 1;
        }
        Console.WriteLine("[OK] Steam remote debugging enabled (flag file)");

        if (filesOnly)
        {
            Console.WriteLine();
            Console.WriteLine("--files-only mode: skipped autostart, stopping old processes and launching.");
            return 0;
        }

        int killed = StopWatchers();
        if (killed > 0) Console.WriteLine("[OK] Stopped previously running watchers: " + killed);

        string startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        string legacy = Path.Combine(startupDir, LegacyStartupFileName);
        if (File.Exists(legacy) && File.ReadAllText(legacy).IndexOf(WatcherScript, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            File.Delete(legacy);
            Console.WriteLine("[OK] Removed old autostart entry (" + LegacyStartupFileName + ") - replaced by the new one");
        }

        string startupFile = Path.Combine(startupDir, StartupFileName);
        string ps1 = Path.Combine(installDir, WatcherScript);
        File.WriteAllText(startupFile,
            "Set sh = CreateObject(\"WScript.Shell\")\r\n" +
            "sh.Run \"powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"\"" + ps1 + "\"\"\", 0, False\r\n",
            Encoding.ASCII);
        Console.WriteLine("[OK] Autostart added: " + startupFile);

        Process.Start(new ProcessStartInfo("wscript.exe", "\"" + startupFile + "\"") { UseShellExecute = false });
        Console.WriteLine("[OK] Watcher started in the background");

        Console.WriteLine();
        EnsureSteamDebugPort(steamDir);

        Console.WriteLine();
        Console.WriteLine("Done. Check your game list: " + gamesPath);
        if (Ask("Open the game list in Notepad?", true))
            Process.Start(new ProcessStartInfo("notepad.exe", "\"" + gamesPath + "\"") { UseShellExecute = false });
        return 0;
    }

    static int Uninstall(string installDir)
    {
        Console.WriteLine("=== Steam Mic Auto - uninstall ===");
        Console.WriteLine();

        int killed = StopWatchers();
        Console.WriteLine("[OK] Stopped watchers: " + killed);

        string startupFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupFileName);
        if (File.Exists(startupFile)) { File.Delete(startupFile); Console.WriteLine("[OK] Removed autostart entry"); }

        if (Directory.Exists(installDir))
        {
            try { Directory.Delete(installDir, true); Console.WriteLine("[OK] Removed folder " + installDir); }
            catch (Exception ex) { Console.WriteLine("[!] Could not remove " + installDir + ": " + ex.Message); }
        }

        string steamDir = FindSteamDir();
        if (steamDir != null)
        {
            string flagPath = Path.Combine(steamDir, FlagFileName);
            if (File.Exists(flagPath) && Ask("Also disable Steam remote debugging (delete the flag file)?", true))
            {
                try { File.Delete(flagPath); Console.WriteLine("[OK] Removed flag file (takes effect after a Steam restart)"); }
                catch (UnauthorizedAccessException)
                {
                    Console.WriteLine("[!] Access denied - delete it manually: " + flagPath);
                }
            }
        }
        Console.WriteLine();
        Console.WriteLine("Note: the Record Microphone toggle in Steam stays in whatever state it was last set to.");
        return 0;
    }

    static bool EnsureFlag(string flagPath)
    {
        if (File.Exists(flagPath)) return true;
        try { File.WriteAllText(flagPath, ""); return true; }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        Console.WriteLine("[..] The Steam folder needs administrator rights - please confirm the UAC prompt");
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
            Console.WriteLine("[OK] Steam is answering on the debug port - everything works.");
            return;
        }

        Process[] steam = Process.GetProcessesByName("steam");
        if (steam.Length == 0)
        {
            Console.WriteLine("[i] Steam is not running. Start it - the debug port will be enabled automatically.");
            return;
        }

        Console.WriteLine("[!] Steam is running but was started before debugging was enabled - it needs a restart.");
        if (!Ask("Restart Steam now? (this also closes running games and the overlay)", false))
        {
            Console.WriteLine("[i] Restart Steam manually (Steam -> Exit, then start it again).");
            return;
        }

        string steamExe = Path.Combine(steamDir, "steam.exe");
        Process.Start(new ProcessStartInfo(steamExe, "-shutdown") { UseShellExecute = false });
        Console.Write("[..] Shutting down Steam");
        for (int i = 0; i < 90 && Process.GetProcessesByName("steam").Length > 0; i++) { Thread.Sleep(500); Console.Write("."); }
        Console.WriteLine();
        Process.Start(new ProcessStartInfo(steamExe) { UseShellExecute = false });
        Console.Write("[..] Starting Steam");
        for (int i = 0; i < 120 && !DebugPortUp(); i++) { Thread.Sleep(1000); Console.Write("."); }
        Console.WriteLine();
        Console.WriteLine(DebugPortUp()
            ? "[OK] Steam is answering on the debug port - everything works."
            : "[!] Steam is not answering on port 8080 yet - wait until it has fully started.");
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
            if (s == null) throw new InvalidOperationException("Missing resource in setup.exe: " + name);
            using (FileStream f = File.Create(destination)) s.CopyTo(f);
        }
    }

    static bool Ask(string question, bool defaultYes)
    {
        if (silent || Console.IsInputRedirected) return false;
        Console.Write(question + (defaultYes ? " [Y/n] " : " [y/N] "));
        ConsoleKeyInfo key = Console.ReadKey();
        Console.WriteLine();
        if (key.Key == ConsoleKey.Enter) return defaultYes;
        return key.KeyChar == 'y' || key.KeyChar == 'Y';
    }

    static void Pause()
    {
        if (silent || Console.IsInputRedirected) return;
        Console.WriteLine();
        Console.Write("Press any key to close...");
        Console.ReadKey(true);
    }
}
