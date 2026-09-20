using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using Microsoft.Win32;

// Installer + uninstaller: Steam Mic Auto (toggles "Record Microphone" in Steam Game Recording while listed games run).
// Arguments: --uninstall | --purge | --silent | --dir <path> | --games-dir <folder or .txt file> | --files-only
//            --create-flag <file> / --delete-flag <file> (internal, used to get admin rights for the Steam folder)
// The installer copies itself to <install folder>\uninstall.exe; a copy named uninstall*.exe always runs in uninstall mode.
static class Program
{
    const string Version = "1.2.0";
    const string StartupFileName = "SteamMicAuto.vbs";
    const string LegacyStartupFileName = "mic-watcher-uruchom.vbs";
    const string FlagFileName = ".cef-enable-remote-debugging";
    const string WatcherScript = "mic-watcher.ps1";
    const string GamesFileName = "games.txt";
    const string GamesArgMarker = "-GamesFile \"\"";
    const string UninstallExeName = "uninstall.exe";
    const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SteamMicAuto";
    const string ProjectUrl = "https://github.com/Ethan-anonim/steam_mic_mute";

    static bool silent;
    static string pendingDeleteDir;

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
        string exePath = Assembly.GetExecutingAssembly().Location;
        bool runAsUninstaller = Path.GetFileName(exePath).StartsWith("uninstall", StringComparison.OrdinalIgnoreCase);

        bool uninstall = runAsUninstaller, filesOnly = false, purge = false;
        string dirArg = null, gamesDirArg = null, createFlag = null, deleteFlag = null;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            if (a == "--uninstall") uninstall = true;
            else if (a == "--purge") { uninstall = true; purge = true; }
            else if (a == "--silent") silent = true;
            else if (a == "--files-only") filesOnly = true;
            else if (a == "--dir" && i + 1 < args.Length) dirArg = args[++i];
            else if (a == "--games-dir" && i + 1 < args.Length) gamesDirArg = args[++i];
            else if (a == "--create-flag" && i + 1 < args.Length) createFlag = args[++i];
            else if (a == "--delete-flag" && i + 1 < args.Length) deleteFlag = args[++i];
        }

        if (createFlag != null)
        {
            try { File.WriteAllText(createFlag, ""); return 0; }
            catch { return 1; }
        }
        if (deleteFlag != null)
        {
            try { File.Delete(deleteFlag); return 0; }
            catch { return 1; }
        }

        string installDir;
        if (dirArg != null) installDir = Path.GetFullPath(dirArg);
        else if (runAsUninstaller) installDir = Path.GetDirectoryName(exePath);
        else installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamMicAuto");

        Console.Title = "Steam Mic Auto - " + (uninstall ? "uninstall" : "setup");
        int code;
        try { code = uninstall ? Uninstall(installDir, purge) : Install(installDir, gamesDirArg, filesOnly); }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("ERROR: " + ex.Message);
            code = 1;
        }
        Pause();
        if (pendingDeleteDir != null) DeleteFolderAfterExit(pendingDeleteDir);
        return code;
    }

    static int Install(string installDir, string gamesDirArg, bool filesOnly)
    {
        Console.WriteLine("=== Steam Mic Auto " + Version + " - setup ===");
        Console.WriteLine();

        string steamDir = FindSteamDir();
        if (steamDir == null)
        {
            Console.WriteLine("[X] Steam (steam.exe) not found. Install Steam and run this program again.");
            return 1;
        }
        Console.WriteLine("[OK] Steam:          " + steamDir);
        Console.WriteLine("[OK] Install folder: " + installDir);
        Console.WriteLine();

        string startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        string startupFile = Path.Combine(startupDir, StartupFileName);
        string existingGames = FindExistingGamesFile(installDir, startupFile);
        string gamesPath = ChooseGamesPath(installDir, gamesDirArg, existingGames);
        Console.WriteLine();

        Directory.CreateDirectory(installDir);
        ExtractResource("payload.mic-watcher.ps1", Path.Combine(installDir, WatcherScript));
        ExtractResource("payload.steam-recording-mic.ps1", Path.Combine(installDir, "steam-recording-mic.ps1"));
        Console.WriteLine("[OK] Scripts installed");

        string uninstallExe = Path.Combine(installDir, UninstallExeName);
        string self = Assembly.GetExecutingAssembly().Location;
        if (!SamePath(self, uninstallExe)) File.Copy(self, uninstallExe, true);
        Console.WriteLine("[OK] Uninstaller: " + uninstallExe);

        PrepareGamesFile(gamesPath, existingGames, Path.Combine(installDir, GamesFileName));

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
            Console.WriteLine("--files-only mode: skipped autostart, Windows app entry, stopping old processes and launching.");
            return 0;
        }

        int killed = StopWatchers();
        if (killed > 0) Console.WriteLine("[OK] Stopped previously running watchers: " + killed);

        string legacy = Path.Combine(startupDir, LegacyStartupFileName);
        if (File.Exists(legacy) && File.ReadAllText(legacy).IndexOf(WatcherScript, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            File.Delete(legacy);
            Console.WriteLine("[OK] Removed old autostart entry (" + LegacyStartupFileName + ") - replaced by the new one");
        }

        string ps1 = Path.Combine(installDir, WatcherScript);
        File.WriteAllText(startupFile,
            "Set sh = CreateObject(\"WScript.Shell\")\r\n" +
            "sh.Run \"powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"\"" + ps1 + "\"\" " +
            GamesArgMarker + gamesPath + "\"\"\", 0, False\r\n",
            Encoding.ASCII);
        Console.WriteLine("[OK] Autostart added: " + startupFile);

        RegisterUninstall(installDir, uninstallExe);
        Console.WriteLine("[OK] Added to Windows Settings -> Apps (Steam Mic Auto)");

        Process.Start(new ProcessStartInfo("wscript.exe", "\"" + startupFile + "\"") { UseShellExecute = false });
        Console.WriteLine("[OK] Watcher started in the background");

        Console.WriteLine();
        EnsureSteamDebugPort(steamDir);

        Console.WriteLine();
        Console.WriteLine("Done. Your game list: " + gamesPath);
        Console.WriteLine("To remove everything later: Windows Settings -> Apps -> Steam Mic Auto -> Uninstall,");
        Console.WriteLine("or run " + uninstallExe);
        if (Ask("Open the game list in Notepad?", true))
            Process.Start(new ProcessStartInfo("notepad.exe", "\"" + gamesPath + "\"") { UseShellExecute = false });
        return 0;
    }

    static int Uninstall(string installDir, bool purge)
    {
        Console.WriteLine("=== Steam Mic Auto - uninstall ===");
        Console.WriteLine();

        bool interactive = !silent && !Console.IsInputRedirected;
        if (interactive && !purge && !Ask("Remove Steam Mic Auto and its files?", true))
        {
            Console.WriteLine("Cancelled - nothing was removed.");
            return 0;
        }

        string startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        string startupFile = Path.Combine(startupDir, StartupFileName);
        string gamesPath = FindExistingGamesFile(installDir, startupFile);

        int killed = StopWatchers();
        Console.WriteLine("[OK] Stopped watchers: " + killed);

        if (File.Exists(startupFile)) { File.Delete(startupFile); Console.WriteLine("[OK] Removed autostart entry"); }
        string legacy = Path.Combine(startupDir, LegacyStartupFileName);
        if (File.Exists(legacy) && File.ReadAllText(legacy).IndexOf(WatcherScript, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            File.Delete(legacy);
            Console.WriteLine("[OK] Removed old autostart entry (" + LegacyStartupFileName + ")");
        }

        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(UninstallKeyPath))
            {
                if (k != null)
                {
                    k.Close();
                    Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, false);
                    Console.WriteLine("[OK] Removed from Windows Settings -> Apps");
                }
            }
        }
        catch { }

        if (gamesPath != null && File.Exists(gamesPath) && !IsInside(gamesPath, installDir))
        {
            if (purge || (interactive && Ask("Also delete your game list (" + gamesPath + ")?", true)))
            {
                File.Delete(gamesPath);
                Console.WriteLine("[OK] Deleted game list: " + gamesPath);
                RemoveIfEmptyAppFolder(Path.GetDirectoryName(gamesPath));
            }
            else
            {
                Console.WriteLine("[i] Your game list was left in place: " + gamesPath);
            }
        }

        if (Directory.Exists(installDir)) RemoveInstallDir(installDir);

        string steamDir = FindSteamDir();
        if (steamDir != null)
        {
            string flagPath = Path.Combine(steamDir, FlagFileName);
            if (File.Exists(flagPath))
            {
                if (purge || (interactive && Ask("Also disable Steam remote debugging (delete the flag file)?", true)))
                {
                    if (DeleteFlag(flagPath)) Console.WriteLine("[OK] Removed flag file (the debug port closes after a Steam restart)");
                    else Console.WriteLine("[!] Could not delete " + flagPath + " - delete it manually");
                }
                else
                {
                    Console.WriteLine("[i] Steam remote debugging flag left in place: " + flagPath);
                    Console.WriteLine("    (delete it manually or run the uninstaller with --purge)");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("Done. The Record Microphone toggle in Steam stays in whatever state it was last set to.");
        return 0;
    }

    static void RegisterUninstall(string installDir, string uninstallExe)
    {
        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKeyPath))
        {
            k.SetValue("DisplayName", "Steam Mic Auto");
            k.SetValue("DisplayVersion", Version);
            k.SetValue("InstallLocation", installDir);
            k.SetValue("DisplayIcon", uninstallExe);
            k.SetValue("UninstallString", "\"" + uninstallExe + "\" --uninstall");
            k.SetValue("QuietUninstallString", "\"" + uninstallExe + "\" --uninstall --silent");
            k.SetValue("URLInfoAbout", ProjectUrl);
            k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
    }

    // The running uninstaller lives inside the install folder, so it deletes everything else now
    // and asks a detached cmd.exe to remove the folder (and this exe) right after the process exits.
    static void RemoveInstallDir(string installDir)
    {
        string self = Assembly.GetExecutingAssembly().Location;
        if (!IsInside(self, installDir))
        {
            try { Directory.Delete(installDir, true); Console.WriteLine("[OK] Removed folder " + installDir); }
            catch (Exception ex) { Console.WriteLine("[!] Could not remove " + installDir + ": " + ex.Message); }
            return;
        }

        foreach (string f in Directory.GetFiles(installDir))
            if (!SamePath(f, self)) { try { File.Delete(f); } catch { } }
        foreach (string d in Directory.GetDirectories(installDir))
            try { Directory.Delete(d, true); } catch { }
        pendingDeleteDir = installDir;
        Console.WriteLine("[OK] Removed files from " + installDir + " (the folder disappears right after this window closes)");
    }

    static void DeleteFolderAfterExit(string dir)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + dir + "\"");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            Process.Start(psi);
        }
        catch { }
    }

    static void RemoveIfEmptyAppFolder(string dir)
    {
        try
        {
            if (dir != null && Directory.Exists(dir) &&
                string.Equals(Path.GetFileName(dir), "SteamMicAuto", StringComparison.OrdinalIgnoreCase) &&
                Directory.GetFileSystemEntries(dir).Length == 0)
                Directory.Delete(dir);
        }
        catch { }
    }

    // Path of the game list used by a previous install (from the autostart entry, else the default location).
    static string FindExistingGamesFile(string installDir, string startupFile)
    {
        try
        {
            if (File.Exists(startupFile))
            {
                string text = File.ReadAllText(startupFile);
                int i = text.IndexOf(GamesArgMarker, StringComparison.Ordinal);
                if (i >= 0)
                {
                    int start = i + GamesArgMarker.Length;
                    int end = text.IndexOf("\"\"", start, StringComparison.Ordinal);
                    if (end > start) return text.Substring(start, end - start);
                }
            }
        }
        catch { }
        string def = Path.Combine(installDir, GamesFileName);
        return File.Exists(def) ? def : null;
    }

    static string ChooseGamesPath(string installDir, string gamesDirArg, string existing)
    {
        if (gamesDirArg != null) return ToGamesFile(gamesDirArg);

        string installDefault = Path.Combine(installDir, GamesFileName);
        if (silent) return existing ?? installDefault;

        string docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SteamMicAuto", GamesFileName);
        string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "SteamMicAuto-games.txt");

        Console.WriteLine("Where should the game list (games.txt) be stored?");
        Console.WriteLine("  [1] " + docs);
        Console.WriteLine("  [2] " + desktop);
        Console.WriteLine("  [3] " + installDefault);
        Console.WriteLine("  [4] Custom folder or file...");
        if (existing != null) Console.WriteLine("  [Enter] Keep current: " + existing);
        Console.Write("Choice" + (existing == null ? " [1]" : "") + ": ");

        string answer = (Console.ReadLine() ?? "").Trim();
        if (answer == "") return existing ?? docs;
        if (answer == "1") return docs;
        if (answer == "2") return desktop;
        if (answer == "3") return installDefault;
        if (answer == "4")
        {
            Console.Write("Folder (or full path to a .txt file): ");
            string custom = (Console.ReadLine() ?? "").Trim();
            if (custom != "") return ToGamesFile(custom);
        }
        Console.WriteLine("[i] Unrecognized choice - using " + (existing ?? docs));
        return existing ?? docs;
    }

    static string ToGamesFile(string path)
    {
        string p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        p = Path.GetFullPath(p);
        return p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? p : Path.Combine(p, GamesFileName);
    }

    static void PrepareGamesFile(string gamesPath, string existing, string installDefault)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(gamesPath));
        if (!File.Exists(gamesPath))
        {
            if (existing != null && File.Exists(existing) && !SamePath(existing, gamesPath))
            {
                File.Copy(existing, gamesPath);
                Console.WriteLine("[OK] Game list moved to: " + gamesPath);
                if (SamePath(existing, installDefault)) File.Delete(existing);
                return;
            }
            File.WriteAllText(gamesPath, DefaultGames, new UTF8Encoding(false));
        }
        Console.WriteLine("[OK] Game list: " + gamesPath);
    }

    static bool SamePath(string a, string b)
    {
        return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    }

    static bool IsInside(string file, string dir)
    {
        string d = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
        return Path.GetFullPath(file).StartsWith(d, StringComparison.OrdinalIgnoreCase);
    }

    static bool EnsureFlag(string flagPath)
    {
        if (File.Exists(flagPath)) return true;
        try { File.WriteAllText(flagPath, ""); return true; }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        Console.WriteLine("[..] The Steam folder needs administrator rights - please confirm the UAC prompt");
        RunElevated("--create-flag", flagPath);
        return File.Exists(flagPath);
    }

    static bool DeleteFlag(string flagPath)
    {
        try { File.Delete(flagPath); return !File.Exists(flagPath); }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        Console.WriteLine("[..] The Steam folder needs administrator rights - please confirm the UAC prompt");
        RunElevated("--delete-flag", flagPath);
        return !File.Exists(flagPath);
    }

    static void RunElevated(string option, string path)
    {
        try
        {
            var psi = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, option + " \"" + path + "\"");
            psi.Verb = "runas";
            psi.UseShellExecute = true;
            using (Process p = Process.Start(psi)) { p.WaitForExit(); }
        }
        catch { }
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
