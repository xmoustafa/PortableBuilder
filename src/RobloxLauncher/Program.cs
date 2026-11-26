using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RobloxPortable
{
    // Roblox portable launcher.
    //  * run       : redirects this PC's Roblox folders to the share, then starts the
    //                newest Versions\version-* client and refreshes the registry values.
    //  * share     : lets the user pick the share/folder, copies the game files there,
    //                creates the per-PC folders and links this device.
    //  * uninstall : removes this device's links and the registry entries we own.
    //
    // The mode and default share are appended to the EXE at build time
    // (;;PB2CFG;;<base64>;;PB2END;;) so no external config file is required.
    internal static class Program
    {
        private static string _baseDir;
        private static string _root;
        private static string _device;
        private static bool _silent;
        private static bool _needsElevation;
        private static bool _rootProvided;
        private static string _profileOverride = "";
        private static bool _shareElevatedOnce;
        private static Mutex _mutex;

        private static readonly StringBuilder _logBuffer = new StringBuilder();

        private const string AppendedMarker = ";;PB2CFG;;";
        private const string AppendedEnd = ";;PB2END;;";
        private const string DefaultRoot = @"\\SERVER\Roblox Portable";

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateSymbolicLink(string lpSymlinkFileName, string lpTargetFileName, uint dwFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(IntPtr hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [STAThread]
        private static int Main(string[] args)
        {
            _baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
            _device = (Environment.MachineName ?? "PC").Trim().ToUpperInvariant();
            if (_device.Length == 0) _device = "PC";

            try
            {
                string modeArg = null;
                string rootArg = null;
                bool noWait = false;
                bool deleteDataArg = false;

                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i] ?? "";
                    if (string.Equals(a, "--mode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        modeArg = args[++i];
                    }
                    else if (string.Equals(a, "--root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        rootArg = args[++i];
                    }
                    else if (string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase))
                    {
                        _silent = true;
                    }
                    else if (string.Equals(a, "--no-wait", StringComparison.OrdinalIgnoreCase))
                    {
                        noWait = true;
                    }
                    else if (string.Equals(a, "--delete-data", StringComparison.OrdinalIgnoreCase))
                    {
                        deleteDataArg = true;
                    }
                    else if (string.Equals(a, "--profile", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        _profileOverride = args[++i].Trim().Trim('"');
                    }
                    else if (string.Equals(a, "--elevated", StringComparison.OrdinalIgnoreCase))
                    {
                        // informational only: the child process is already elevated
                    }
                }

                IniFile ini = LoadConfig();
                _silent = _silent || ini.GetBool("App", "Silent", false);

                if (!string.IsNullOrEmpty(_profileOverride) && !Directory.Exists(_profileOverride))
                {
                    Fail("Profile folder not found: " + _profileOverride);
                    return 12;
                }

                string mode = (modeArg ?? ini.Get("App", "Mode", "run")).Trim().ToLowerInvariant();
                _root = NormalizeRoot(rootArg);
                _rootProvided = _root.Length > 0;
                if (_root.Length == 0) _root = NormalizeRoot(ReadShareConfig());
                if (_root.Length == 0) _root = NormalizeRoot(ini.Get("Share", "Root", ""));

                // RequireAdmin=1 forces a UAC prompt; "auto" (default) elevates
                // only when a symbolic link has to be created/updated.
                string adminMode = ini.Get("App", "RequireAdmin", "auto").Trim().ToLowerInvariant();
                bool alwaysAdmin = adminMode == "1" || adminMode == "true" ||
                                   adminMode == "yes" || adminMode == "on";
                if (alwaysAdmin && !IsElevated())
                {
                    Log("Administrator rights requested; relaunching with UAC.");
                    FlushLog();
                    if (RelaunchElevated(mode, _root, noWait)) return 0;
                    Fail("Administrator rights are required for Roblox Portable.");
                    return 5;
                }

                Log("=== Roblox Portable (" + mode + ") ===");
                Log("Device: " + _device);
                Log("Package: " + _baseDir);
                Log("Share: " + (_root.Length == 0 ? "(not configured)" : _root));

                if (mode == "prepare") return PrepareMode();
                if (mode == "share") return ShareMode(ini);
                if (mode == "uninstall") return UninstallMode(ini, deleteDataArg);
                return RunMode(ini, noWait);
            }
            catch (Exception ex)
            {
                Log("FATAL: " + ex);
                Fail("Roblox Portable failed.\r\n\r\n" + ex.Message);
                return 1;
            }
            finally
            {
                ReleaseMutex();
                FlushLog();
            }
        }

        // ------------------------------------------------------------------
        //  prepare (runs elevated, only touches the links)
        // ------------------------------------------------------------------

        private static int PrepareMode()
        {
            if (string.IsNullOrEmpty(_root) || !IsRootAccessible(_root))
            {
                Fail("Share not configured or not accessible.");
                return 8;
            }
            string error;
            if (!PrepareDevice(_root, out error))
            {
                Fail(error);
                return 8;
            }
            Log("Device links prepared for " + _device + ".");
            return 0;
        }

        // ------------------------------------------------------------------
        //  run
        // ------------------------------------------------------------------

        private static int RunMode(IniFile ini, bool noWait)
        {
            if (!AcquireMutex())
            {
                Info("Roblox Portable is already running on this PC.");
                return 0;
            }

            if (!IsRootAccessible(_root))
            {
                Log("Share not reachable; opening the sharing setup.");
                if (!ShareModeInteractive(ini))
                {
                    Fail("No accessible share was configured.");
                    return 7;
                }
            }

            string error;
            if (!PrepareDevice(_root, out error))
            {
                if (_needsElevation && !IsElevated())
                {
                    Log("Elevation required for symbolic links; preparing them with admin.");
                    FlushLog();
                    if (RelaunchElevatedAndWait(ProfileRoot()))
                    {
                        _needsElevation = false;
                        if (!PrepareDevice(_root, out error))
                        {
                            Fail(error);
                            return 8;
                        }
                    }
                    else
                    {
                        Fail("Administrator rights are required to create the folder links.");
                        return 5;
                    }
                }
                else
                {
                    Fail(error);
                    return 8;
                }
            }

            if (!EnsureGameFiles())
            {
                Fail("Could not prepare the shared game files.\r\n\r\n" +
                     "Close Roblox (or any tool using the files) and run Enable_Sharing.exe again.\r\n" +
                     "Log: " + LogPathForDisplay());
                return 9;
            }

            string versionDir;
            string exe;
            if (!FindLatestVersion(out versionDir, out exe))
            {
                Fail("Roblox client not found.\r\n\r\nExpected:\r\n" + Path.Combine(_root, "Versions", "version-*", "RobloxPlayerBeta.exe") +
                     "\r\n\r\nRun Enable_Sharing.exe to copy the game files to the share.");
                return 9;
            }

            UpdateRegistry(versionDir, exe);

            string arguments = BuildGameArguments();
            Log("Launching: " + exe + (arguments.Length == 0 ? "" : " " + arguments));

            ProcessStartInfo psi = new ProcessStartInfo(exe);
            psi.WorkingDirectory = versionDir;
            psi.UseShellExecute = true;
            if (arguments.Length > 0) psi.Arguments = arguments;

            using (Process game = Process.Start(psi))
            {
                if (game == null)
                {
                    Fail("Could not start Roblox.");
                    return 10;
                }

                if (noWait) return 0;

                Log("Waiting for the game to exit...");
                FlushLog();
                game.WaitForExit();
                Log("Game exited with code " + game.ExitCode + ".");
            }
            SaveLocalLowSettings();
            return 0;
        }

        // Collects the arguments that were passed to us (e.g. a roblox-player:
        // protocol URI) so the browser hand-off keeps working.
        private static string BuildGameArguments()
        {
            string[] args = Environment.GetCommandLineArgs();
            StringBuilder sb = new StringBuilder();
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i] ?? "";
                if (string.Equals(a, "--mode", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "--root", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "--profile", StringComparison.OrdinalIgnoreCase))
                {
                    i++;
                    continue;
                }
                if (a.StartsWith("--", StringComparison.Ordinal)) continue;

                if (sb.Length > 0) sb.Append(' ');
                if (a.IndexOf(' ') >= 0) sb.Append('"').Append(a).Append('"');
                else sb.Append(a);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        //  share / device preparation
        // ------------------------------------------------------------------

        private static int ShareMode(IniFile ini)
        {
            if (!AcquireMutex())
            {
                Info("Roblox Portable is already running on this PC.");
                return 0;
            }

            bool ok = ShareModeInteractive(ini);
            if (!ok) return 7;

            if (_needsElevation && !IsElevated())
            {
                if (_shareElevatedOnce)
                {
                    Fail("The folder links still could not be created. Check the share permissions.");
                    return 5;
                }
                _shareElevatedOnce = true;

                Log("Elevation required for the links; preparing them with admin.");
                FlushLog();
                if (!RelaunchElevatedAndWait(ProfileRoot()))
                {
                    Fail("Administrator rights are required to create the folder links.");
                    return 5;
                }

                _needsElevation = false;
                _rootProvided = true; // reuse the already chosen share, do not re-prompt

                if (!ShareModeInteractive(ini)) return 7;
                if (_needsElevation)
                {
                    Fail("The folder links could not be created after elevation.");
                    return 5;
                }
            }
            return 0;
        }

        // Lets the user choose the share, writes share.cfg, copies the game files
        // and links this device. Returns true when the share is ready.
        private static bool ShareModeInteractive(IniFile ini)
        {
            string root = _root;
            if (root.Length == 0) root = DefaultRoot;

            if (!_silent && !_rootProvided)
            {
                using (ShareForm form = new ShareForm(root, _device, ini.Get("App", "Name", "Roblox Portable")))
                {
                    if (form.ShowDialog() != DialogResult.OK) return false;
                    root = form.SelectedRoot;
                }
            }

            root = NormalizeRoot(root);
            if (root.Length == 0)
            {
                Fail("Choose a share or folder first.");
                return false;
            }

            string testError;
            if (!TestShare(root, out testError))
            {
                Fail(testError);
                return false;
            }

            _root = root;
            try
            {
                WriteShareConfig(root);
                Log("share.cfg written: " + root);
            }
            catch (Exception ex)
            {
                Fail("Could not write share.cfg next to the launcher:\r\n" + ex.Message);
                return false;
            }

            // Link this device first: the symlink probe runs before any long
            // game-file copy, so the UAC prompt (if needed) comes early and no
            // data is moved before we know the links can be created.
            string error;
            bool linksReady = PrepareDevice(root, out error);
            if (!linksReady)
            {
                if (_needsElevation)
                {
                    Log("Links need elevation: " + error);
                    return true; // ShareMode() relaunches elevated
                }
                Fail(error);
                return false;
            }
            Log("Device linked: " + _device);

            bool filesReady;
            if (!_silent)
            {
                using (ProgressForm progress = new ProgressForm("Preparing the shared game files..."))
                {
                    progress.Show();
                    Application.DoEvents();
                    filesReady = EnsureGameFiles(progress.SetStatus);
                }
            }
            else
            {
                filesReady = EnsureGameFiles(null);
            }

            if (!filesReady)
            {
                Fail("Could not copy the Roblox game files to the share.\r\n\r\n" +
                     "Close Roblox (or any tool using the files) and run Enable_Sharing.exe again.\r\n" +
                     "Log: " + LogPathForDisplay());
                return false;
            }

            if (!_silent)
            {
                string info = "Sharing is ready.\r\n\r\n" +
                              "Share: " + root + "\r\n" +
                              "Device: " + _device + "\r\n\r\n" +
                              "Run Roblox.exe on this PC to play. Other PCs on the network run\r\n" +
                              "Enable_Sharing.exe once, then Roblox.exe.\r\n\r\n" +
                              "Log: " + LogPathForDisplay();
                MessageBox.Show(info, "Roblox Portable - Enable Sharing",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return true;
        }

        private static bool TestShare(string root, out string error)
        {
            error = null;
            try
            {
                Directory.CreateDirectory(root);
            }
            catch (Exception ex)
            {
                error = "The share/folder is not accessible:\r\n" + root + "\r\n\r\n" + ex.Message;
                return false;
            }

            string probe = Path.Combine(root, ".pb_write_test_" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
            }
            catch (Exception ex)
            {
                error = "The share/folder is read-only:\r\n" + root + "\r\n\r\n" + ex.Message;
                return false;
            }
            return true;
        }

        // Creates <root>\Data\{Local,Roaming,ProgramData,LocalLow}\<PC>\... and
        // links the real Windows folders to them.
        private static bool PrepareDevice(string root, out string error)
        {
            error = null;
            try
            {
                string local = LocalAppDataPath();
                string roaming = RoamingPath();
                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

                string data = Path.Combine(root, "Data");
                string localRoot = Path.Combine(data, "Local", _device);
                string roamingRoot = Path.Combine(data, "Roaming", _device);
                string programDataRoot = Path.Combine(data, "ProgramData", _device);
                string localLowRoot = Path.Combine(data, "LocalLow", _device);

                string[] targets =
                {
                    Path.Combine(localRoot, "Roblox"),
                    Path.Combine(localRoot, "Temp", "Roblox"),
                    Path.Combine(roamingRoot, "Roblox"),
                    Path.Combine(programDataRoot, "Roblox"),
                    Path.Combine(data, "Logs")
                };
                foreach (string dir in targets)
                {
                    try { Directory.CreateDirectory(dir); }
                    catch (UnauthorizedAccessException) { _needsElevation = true; throw; }
                }

                List<string> links = new List<string>();
                links.Add(Path.Combine(local, "Roblox") + "|" + Path.Combine(localRoot, "Roblox"));
                links.Add(Path.Combine(local, "Temp", "Roblox") + "|" + Path.Combine(localRoot, "Temp", "Roblox"));
                links.Add(Path.Combine(roaming, "Roblox") + "|" + Path.Combine(roamingRoot, "Roblox"));
                links.Add(Path.Combine(programData, "Roblox") + "|" + Path.Combine(programDataRoot, "Roblox"));

                // If any link must be (re)created, make sure we can create
                // symlinks before moving local data to the share.
                bool needCreate = false;
                foreach (string entry in links)
                {
                    int bar = entry.IndexOf('|');
                    string real = entry.Substring(0, bar);
                    string target = entry.Substring(bar + 1);
                    if (!IsReparsePoint(real) || !SamePath(GetLinkTarget(real), target))
                    {
                        needCreate = true;
                        break;
                    }
                }

                if (needCreate && !IsElevated() && !CanCreateSymlinks())
                {
                    _needsElevation = true;
                    error = "Administrator rights are required to create the folder links.";
                    return false;
                }

                foreach (string entry in links)
                {
                    int bar = entry.IndexOf('|');
                    string real = entry.Substring(0, bar);
                    string target = entry.Substring(bar + 1);
                    if (!EnsureLink(real, target, out error))
                        return false;
                }

                // LocalLow is a single settings file: copy it per device instead
                // of linking the folder (matches the Roblox deployment spec).
                SyncLocalLowSettings(localLowRoot);
                return true;
            }
            catch (Exception ex)
            {
                if (ex is UnauthorizedAccessException) _needsElevation = true;
                error = "Could not prepare this device:\r\n" + ex.Message;
                return false;
            }
        }

        // rbxcsettings.rbx lives directly in LocalLow and is copied per device
        // (not linked). First run adopts the local file; later runs apply the
        // share copy for this PC.
        private static void SyncLocalLowSettings(string shareDir)
        {
            try
            {
                string localLow = LocalLowPath(LocalAppDataPath());
                string localFile = Path.Combine(localLow, "rbxcsettings.rbx");
                string shareFile = Path.Combine(shareDir, "rbxcsettings.rbx");

                Directory.CreateDirectory(shareDir);

                bool haveLocal = File.Exists(localFile);
                bool haveShare = File.Exists(shareFile);

                if (haveLocal && !haveShare)
                {
                    try
                    {
                        File.Move(localFile, shareFile);
                        Log("LocalLow settings adopted into the share.");
                        return;
                    }
                    catch (Exception ex) { Log("LocalLow adopt failed: " + ex.Message); }
                }

                if (haveLocal && haveShare)
                {
                    // Never overwrite the newer side. This preserves changes made
                    // in a previous --no-wait session or after a crash.
                    DateTime localStamp = File.GetLastWriteTimeUtc(localFile);
                    DateTime shareStamp = File.GetLastWriteTimeUtc(shareFile);
                    if (localStamp > shareStamp.AddSeconds(2))
                    {
                        File.Copy(localFile, shareFile, true);
                        Log("LocalLow settings newer locally; saved to the share.");
                        return;
                    }
                }

                if (File.Exists(shareFile))
                {
                    Directory.CreateDirectory(localLow);
                    File.Copy(shareFile, localFile, true);
                    Log("LocalLow settings applied for " + _device + ".");
                }
            }
            catch (Exception ex) { Log("LocalLow settings sync failed: " + ex.Message); }
        }

        private static void SaveLocalLowSettings()
        {
            try
            {
                if (string.IsNullOrEmpty(_root)) return;
                string localLow = LocalLowPath(LocalAppDataPath());
                string localFile = Path.Combine(localLow, "rbxcsettings.rbx");
                if (!File.Exists(localFile)) return;

                string shareDir = Path.Combine(_root, "Data", "LocalLow", _device);
                Directory.CreateDirectory(shareDir);
                string shareFile = Path.Combine(shareDir, "rbxcsettings.rbx");
                if (File.Exists(shareFile))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(shareFile) > File.GetLastWriteTimeUtc(localFile))
                        {
                            Log("LocalLow settings were not saved back (share copy is newer).");
                            return;
                        }
                    }
                    catch { }
                }
                File.Copy(localFile, shareFile, true);
                Log("LocalLow settings saved back to the share.");
            }
            catch (Exception ex) { Log("LocalLow settings save failed: " + ex.Message); }
        }

        private static bool EnsureLink(string real, string target, out string error)
        {
            error = null;
            try
            {
                Directory.CreateDirectory(target);

                if (IsReparsePoint(real))
                {
                    string actual = GetLinkTarget(real);
                    if (SamePath(actual, target))
                    {
                        Log("Link ok: " + real + " -> " + target);
                        return true;
                    }

                    // Share changed: bring the old per-device data along.
                    if (!string.IsNullOrEmpty(actual) && Directory.Exists(actual) && !SamePath(actual, target))
                    {
                        Log("Migrating data from the previous share: " + actual);
                        if (!CopyMissing(actual, target, 0))
                        {
                            error = "Could not copy the previous Roblox data to the new share.\r\n" +
                                    "The old link was left in place. Close Roblox and try again.";
                            return false;
                        }
                    }

                    Log("Replacing link: " + real + " (was " + (actual ?? "unreadable") + ")");
                    if (!TryDeleteLink(real))
                    {
                        error = "Could not replace the existing link:\r\n" + real;
                        return false;
                    }
                }
                else if (Directory.Exists(real))
                {
                    Log("Adopting local data into the share: " + real);
                    if (!MergeMove(real, target, 0))
                    {
                        error = "Could not move this folder to the share (a file is in use):\r\n" + real;
                        return false;
                    }
                }
                else if (File.Exists(real))
                {
                    error = "A file occupies the Roblox data path:\r\n" + real;
                    return false;
                }

                string parent = Path.GetDirectoryName(real);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                if (!CreateSymbolicLink(real, target, 1))
                {
                    int code = Marshal.GetLastWin32Error();
                    if (code == 1314) _needsElevation = true;
                    error = "Creating the symbolic link failed (Win32 " + code + "):\r\n" + real +
                            "\r\n-> " + target +
                            (code == 1314 ? "\r\n\r\nAdministrator rights are required." : "");
                    Log("mklink failed (" + code + "): " + real + " -> " + target);
                    return false;
                }

                Log("Link created: " + real + " -> " + target);
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                _needsElevation = true;
                error = "Access denied while linking " + real + ":\r\n" + ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = "Linking failed for " + real + ":\r\n" + ex.Message;
                return false;
            }
        }

        // Moves local content into the share without deleting anything that
        // already exists there. The share copy always wins on collisions.
        private static bool MergeMove(string source, string target, int depth)
        {
            if (depth > 32) return false;
            try
            {
                Directory.CreateDirectory(target);

                foreach (string file in Directory.GetFiles(source))
                {
                    string name = Path.GetFileName(file);
                    string dest = Path.Combine(target, name);
                    if (File.Exists(dest))
                    {
                        // Keep the local duplicate instead of deleting it.
                        string backup = UniquePath(dest + ".local.bak");
                        try
                        {
                            File.Move(file, backup);
                            Log("Adopt: duplicate kept as " + Path.GetFileName(backup));
                        }
                        catch (Exception ex)
                        {
                            Log("Adopt: could not keep duplicate " + name + " : " + ex.Message);
                            return false;
                        }
                        continue;
                    }
                    try
                    {
                        File.Move(file, dest);
                    }
                    catch (IOException)
                    {
                        try { File.Copy(file, dest, false); File.Delete(file); }
                        catch { return false; }
                    }
                }

                foreach (string dir in Directory.GetDirectories(source))
                {
                    string name = Path.GetFileName(dir);
                    if (IsReparsePoint(dir))
                    {
                        Log("Adopt: nested link skipped: " + dir);
                        continue;
                    }
                    if (!MergeMove(dir, Path.Combine(target, name), depth + 1)) return false;
                }

                Directory.Delete(source, false);
                return true;
            }
            catch (Exception ex)
            {
                Log("Adopt failed: " + source + " : " + ex.Message);
                return false;
            }
        }

        private static string UniquePath(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            for (int i = 2; i < 1000; i++)
            {
                string candidate = path + "." + i;
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }
            return path + "." + Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        // Copies missing entries without deleting the source (used when the
        // user moves the share to a new location).
        private static bool CopyMissing(string source, string target, int depth)
        {
            if (depth > 32) return false;
            bool ok = true;
            try
            {
                Directory.CreateDirectory(target);
                foreach (string file in Directory.GetFiles(source))
                {
                    string dest = Path.Combine(target, Path.GetFileName(file));
                    if (File.Exists(dest)) continue;
                    try { File.Copy(file, dest, false); }
                    catch (Exception ex) { ok = false; Log("Migration copy failed: " + file + " : " + ex.Message); }
                }
                foreach (string dir in Directory.GetDirectories(source))
                {
                    if (IsReparsePoint(dir)) continue;
                    if (!CopyMissing(dir, Path.Combine(target, Path.GetFileName(dir)), depth + 1)) ok = false;
                }
            }
            catch (Exception ex) { ok = false; Log("Migration copy failed: " + ex.Message); }
            return ok;
        }

        private static bool TryDeleteLink(string path)
        {
            try
            {
                Directory.Delete(path, false);
                return true;
            }
            catch (Exception ex)
            {
                Log("Could not delete link " + path + ": " + ex.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------
        //  uninstall
        // ------------------------------------------------------------------

        private static int UninstallMode(IniFile ini, bool deleteDataArg)
        {
            bool deleteData = deleteDataArg;
            bool confirmed = _silent;

            if (!_silent)
            {
                string text = "Remove this PC's Roblox Portable links and registry entries?\r\n\r\n" +
                              "Device: " + _device + "\r\n" +
                              "Share: " + (_root.Length == 0 ? "(not configured)" : _root) + "\r\n\r\n" +
                              "The shared game files are kept. Your local Roblox data stays on the share." +
                              "\r\n\r\nDelete this device's shared data as well?";
                DialogResult answer = MessageBox.Show(text, "Roblox Portable - Uninstall",
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button3);
                if (answer == DialogResult.Cancel) { Log("Uninstall cancelled."); return 0; }
                confirmed = true;
                deleteData = (answer == DialogResult.Yes);
            }

            if (!confirmed) return 0;

            int removed = 0;
            int failed = 0;
            foreach (string path in DevicePaths())
            {
                if (!IsReparsePoint(path))
                {
                    Log("Not a link, left alone: " + path);
                    continue;
                }

                string target = GetLinkTarget(path);
                if (string.IsNullOrEmpty(target) || !IsShareOwned(target))
                {
                    Log("Link is not owned by Roblox Portable, left alone: " + path +
                        " -> " + (target ?? "unreadable"));
                    continue;
                }

                if (TryDeleteLink(path))
                {
                    removed++;
                    Log("Removed link: " + path);
                }
                else
                {
                    failed++;
                }
            }

            // Links created with another admin account may need elevation.
            if (failed > 0 && !IsElevated())
            {
                Log("Elevation required to remove " + failed + " link(s); relaunching.");
                FlushLog();
                if (RelaunchElevated("uninstall", _root, false, true,
                        (deleteData ? " --delete-data" : "") + " --profile " + Quote(ProfileRoot())))
                    return 0;
                Log("Elevation was declined; " + failed + " link(s) were left in place.");
            }

            DeleteOwnedRegistry(ini);

            if (deleteData)
            {
                if (failed > 0)
                {
                    Log("Shared data was not deleted because " + failed + " link(s) are still present.");
                }
                else if (string.IsNullOrEmpty(_root))
                {
                    Log("No share configured; nothing to delete.");
                }
                else
                {
                    string data = Path.Combine(_root, "Data");
                    foreach (string sub in new string[] { "Local", "Roaming", "ProgramData", "LocalLow" })
                    {
                        string dir = Path.Combine(data, sub, _device);
                        try
                        {
                            if (Directory.Exists(dir))
                            {
                                Directory.Delete(dir, true);
                                Log("Deleted shared data: " + dir);
                            }
                        }
                        catch (Exception ex) { Log("Could not delete " + dir + ": " + ex.Message); }
                    }
                }
            }

            if (!_silent)
            {
                MessageBox.Show("Uninstall finished.\r\n\r\nLinks removed: " + removed +
                    "\r\n\r\nRoblox.exe and Enable_Sharing.exe were left in place.",
                    "Roblox Portable - Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return 0;
        }

        private static bool IsShareOwned(string path)
        {
            string p = (path ?? "").Replace('/', '\\');
            if (string.IsNullOrEmpty(_root) || !IsUnderRootPath(p, _root)) return false;
            return p.IndexOf(@"\Data\Local\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   p.IndexOf(@"\Data\Roaming\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   p.IndexOf(@"\Data\ProgramData\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   p.IndexOf(@"\Data\LocalLow\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsUnderRootPath(string path, string root)
        {
            try
            {
                string r = Path.GetFullPath(root).TrimEnd('\\');
                string p = Path.GetFullPath(path).TrimEnd('\\');
                return string.Equals(p, r, StringComparison.OrdinalIgnoreCase) ||
                       p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static IEnumerable<string> DevicePaths()
        {
            string local = LocalAppDataPath();
            string roaming = RoamingPath();
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string localLow = LocalLowPath(local);

            yield return Path.Combine(local, "Roblox");
            yield return Path.Combine(local, "Temp", "Roblox");
            yield return Path.Combine(roaming, "Roblox");
            yield return Path.Combine(programData, "Roblox");
            yield return Path.Combine(localLow, "Roblox");
        }

        // ------------------------------------------------------------------
        //  registry
        // ------------------------------------------------------------------

        private static readonly string[] ManagedRegistryKeys =
        {
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Roblox",
            @"Software\Classes\roblox-player",
            @"Software\Classes\roblox"
        };

        private static void UpdateRegistry(string versionDir, string exe)
        {
            if (!BackupRegistryOnce())
            {
                Log("Registry backup failed; skipping the registry update (nothing was changed).");
                return;
            }

            string versionName = Path.GetFileName(versionDir);
            if (versionName.StartsWith("version-", StringComparison.OrdinalIgnoreCase))
                versionName = versionName.Substring("version-".Length);

            string uninstaller = Path.Combine(_baseDir, "Uninstall.exe");
            // Always route browser/protocol launches through this launcher so a
            // new version is picked up even if nobody started the game manually.
            string command = Quote(Application.ExecutablePath) +
                             " --no-wait -protocol \"%1\"";

            const string uninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Roblox";
            SetString(uninstallKey, "DisplayName", "Roblox");
            SetString(uninstallKey, "Publisher", "Roblox");
            SetString(uninstallKey, "DisplayVersion", versionName);
            SetString(uninstallKey, "DisplayIcon", exe);
            SetString(uninstallKey, "InstallLocation", Path.GetDirectoryName(versionDir));
            SetString(uninstallKey, "UninstallString", Quote(uninstaller));
            SetDword(uninstallKey, "NoModify", 1);
            SetDword(uninstallKey, "NoRepair", 1);

            SetString(@"Software\Classes\roblox-player", "", "URL:Roblox Protocol");
            SetString(@"Software\Classes\roblox-player", "URL Protocol", "");
            SetString(@"Software\Classes\roblox-player\shell\open\command", "", command);

            SetString(@"Software\Classes\roblox", "", "URL:Roblox Protocol");
            SetString(@"Software\Classes\roblox", "URL Protocol", "");
            SetString(@"Software\Classes\roblox\shell\open\command", "", command);

            Log("Registry updated for " + versionName + ".");
        }

        private static void SetString(string key, string name, string value)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(key))
                {
                    if (k == null) return;
                    object current = k.GetValue(name, null);
                    if (current != null && string.Equals(Convert.ToString(current), value, StringComparison.Ordinal))
                        return;
                    k.SetValue(name, value, RegistryValueKind.String);
                }
            }
            catch (Exception ex) { Log("Registry write failed: " + key + "\\" + name + " : " + ex.Message); }
        }

        private static void SetDword(string key, string name, int value)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(key))
                {
                    if (k == null) return;
                    object current = k.GetValue(name, null);
                    if (current is int && (int)current == value) return;
                    k.SetValue(name, value, RegistryValueKind.DWord);
                }
            }
            catch (Exception ex) { Log("Registry write failed: " + key + "\\" + name + " : " + ex.Message); }
        }

        private static void DeleteOwnedRegistry(IniFile ini)
        {
            try
            {
                if (RestoreRegistryBackup())
                {
                    Log("Registry restored to its pre-portable state.");
                    return;
                }

                // No backup (Roblox.exe was never run): remove only what points at us.
                const string uninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Roblox";
                bool removeUninstall = false;
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(uninstallKey))
                {
                    if (k != null)
                    {
                        string install = Convert.ToString(k.GetValue("InstallLocation", ""));
                        string uninstaller = Convert.ToString(k.GetValue("UninstallString", ""));
                        removeUninstall = ContainsOwnPath(install) || ContainsOwnPath(uninstaller);
                    }
                }
                if (removeUninstall)
                {
                    Registry.CurrentUser.DeleteSubKeyTree(uninstallKey, false);
                    Log("Registry key removed: " + uninstallKey);
                }
                else
                {
                    Log("Registry key is not ours, left alone: " + uninstallKey);
                }

                DeleteProtocolKey(@"Software\Classes\roblox-player");
                DeleteProtocolKey(@"Software\Classes\roblox");
            }
            catch (Exception ex) { Log("Registry cleanup failed: " + ex.Message); }
        }

        // Captures the original registry state once per device so uninstall can
        // restore it instead of destroying a pre-existing Roblox installation.
        private static bool BackupRegistryOnce()
        {
            try
            {
                string dir = RegistryBackupDir();
                if (dir.Length == 0) return false;

                string state = Path.Combine(dir, "state.txt");
                if (File.Exists(state)) return BackupIsComplete(dir, state);

                Directory.CreateDirectory(dir);
                StringBuilder sb = new StringBuilder();
                string reg = Path.Combine(Environment.SystemDirectory, "reg.exe");

                foreach (string key in ManagedRegistryKeys)
                {
                    string full = @"HKCU\" + key;
                    string file = Path.Combine(dir, BackupFileName(key));
                    int code = RunToolQuiet(reg, "export " + Quote(full) + " " + Quote(file) + " /y");
                    if (code == 0)
                    {
                        sb.AppendLine(full + "|present");
                    }
                    else
                    {
                        sb.AppendLine(full + "|absent");
                        try { if (File.Exists(file)) File.Delete(file); } catch { }
                    }
                }

                File.WriteAllText(state, sb.ToString(), Encoding.UTF8);
                Log("Registry backup saved: " + dir);

                // Keep a local copy when the backup lives on the share.
                string local = LocalRegistryBackupDir();
                if (!SamePath(dir, local))
                {
                    try
                    {
                        Directory.CreateDirectory(local);
                        foreach (string file in Directory.GetFiles(dir))
                            File.Copy(file, Path.Combine(local, Path.GetFileName(file)), true);
                    }
                    catch { }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log("Registry backup failed: " + ex.Message);
                return false;
            }
        }

        private static bool BackupIsComplete(string dir, string state)
        {
            try
            {
                foreach (string raw in File.ReadAllLines(state))
                {
                    string line = (raw ?? "").Trim();
                    int bar = line.LastIndexOf('|');
                    if (bar <= 0) continue;
                    if (!string.Equals(line.Substring(bar + 1).Trim(), "present",
                            StringComparison.OrdinalIgnoreCase)) continue;

                    string full = line.Substring(0, bar);
                    string key = full.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase)
                        ? full.Substring(5) : full;
                    if (!File.Exists(Path.Combine(dir, BackupFileName(key))))
                    {
                        Log("Existing registry backup is incomplete; refusing to overwrite it.");
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log("Could not verify the registry backup: " + ex.Message);
                return false;
            }
        }

        private static bool RestoreRegistryBackup()
        {
            string dir = FindRegistryBackupDir();
            if (dir.Length == 0) return false;

            string state = Path.Combine(dir, "state.txt");
            if (!File.Exists(state)) return false;

            string reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
            bool ok = true;
            int handled = 0;

            try
            {
                foreach (string raw in File.ReadAllLines(state))
                {
                    string line = (raw ?? "").Trim();
                    if (line.Length == 0) continue;
                    int bar = line.LastIndexOf('|');
                    if (bar <= 0) continue;

                    handled++;
                    string full = line.Substring(0, bar);
                    string status = line.Substring(bar + 1).Trim().ToLowerInvariant();
                    string key = full.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase) ? full.Substring(5) : full;

                    if (status == "present")
                    {
                        string file = Path.Combine(dir, BackupFileName(key));
                        if (File.Exists(file))
                        {
                            int code = RunTool(reg, "import " + Quote(file));
                            Log("Registry restore (exit " + code + "): " + key);
                            if (code != 0) ok = false;
                        }
                        else
                        {
                            Log("Backup file missing for " + key + "; keeping the backup.");
                            ok = false;
                        }
                    }
                    else if (status == "absent")
                    {
                        try
                        {
                            Registry.CurrentUser.DeleteSubKeyTree(key, false);
                            Log("Registry key removed (was absent before): " + key);
                        }
                        catch (Exception ex) { Log("Could not remove " + key + ": " + ex.Message); }
                    }
                }
            }
            catch (Exception ex)
            {
                ok = false;
                Log("Registry restore failed: " + ex.Message);
            }

            if (handled == 0)
            {
                Log("Registry backup state is empty; keeping it for inspection.");
                return true;
            }
            if (!ok)
            {
                Log("Registry restore was incomplete; the backup was kept for a retry.");
                return true;
            }

            try { Directory.Delete(dir, true); } catch { }
            DeleteEmptyParents(dir);
            string local = LocalRegistryBackupDir();
            if (!SamePath(dir, local))
            {
                try { Directory.Delete(local, true); } catch { }
                DeleteEmptyParents(local);
            }
            return true;
        }

        private static void DeleteEmptyParents(string dir)
        {
            try
            {
                string parent = Path.GetDirectoryName(dir);
                for (int i = 0; i < 2 && !string.IsNullOrEmpty(parent); i++)
                {
                    if (Directory.Exists(parent) && Directory.GetFileSystemEntries(parent).Length == 0)
                    {
                        Directory.Delete(parent, false);
                        parent = Path.GetDirectoryName(parent);
                    }
                    else break;
                }
            }
            catch { }
        }

        private static string RegistryBackupDir()
        {
            if (!string.IsNullOrEmpty(_root) && IsRootAccessible(_root))
            {
                try { return Path.Combine(_root, "Data", "Registry", _device); }
                catch { }
            }
            return LocalRegistryBackupDir();
        }

        private static string LocalRegistryBackupDir()
        {
            try
            {
                return Path.Combine(LocalAppDataPath(), "RobloxPortable", "Registry", _device);
            }
            catch { return ""; }
        }

        private static string FindRegistryBackupDir()
        {
            // Prefer the per-user copy: it is not writable by other users on the
            // share, so a tampered package cannot replace the restore state.
            string local = LocalRegistryBackupDir();
            bool localExists = local.Length > 0 && File.Exists(Path.Combine(local, "state.txt"));
            bool localComplete = localExists && BackupIsComplete(local, Path.Combine(local, "state.txt"));

            string share = "";
            if (!string.IsNullOrEmpty(_root) && IsRootAccessible(_root))
            {
                try { share = Path.Combine(_root, "Data", "Registry", _device); }
                catch { share = ""; }
            }
            bool shareExists = share.Length > 0 && File.Exists(Path.Combine(share, "state.txt"));
            bool shareComplete = shareExists && BackupIsComplete(share, Path.Combine(share, "state.txt"));

            if (localComplete) return local;
            if (shareComplete) return share;
            // Keep an incomplete backup so the restore reports the problem and
            // does not fall back to blindly deleting the registry keys.
            if (localExists) return local;
            if (shareExists) return share;
            return "";
        }

        private static string BackupFileName(string key)
        {
            string name = (key ?? "").Replace('\\', '_').Replace(':', '_');
            return name + ".reg";
        }

        private static int RunTool(string file, string arguments)
        {
            return RunTool(file, arguments, false);
        }

        private static int RunToolQuiet(string file, string arguments)
        {
            return RunTool(file, arguments, true);
        }

        private static int RunTool(string file, string arguments, bool quiet)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file, arguments);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    System.Threading.Tasks.Task<string> outTask = p.StandardOutput.ReadToEndAsync();
                    System.Threading.Tasks.Task<string> errTask = p.StandardError.ReadToEndAsync();
                    bool exited = p.WaitForExit(120000);
                    if (!exited)
                    {
                        try { p.Kill(); } catch { }
                        try { p.WaitForExit(5000); } catch { }
                    }
                    string stdout = "";
                    string stderr = "";
                    try { if (outTask.Wait(5000)) stdout = outTask.Result; } catch { }
                    try { if (errTask.Wait(5000)) stderr = errTask.Result; } catch { }
                    if (!exited)
                    {
                        if (!quiet) Log("Tool timed out: " + Path.GetFileName(file));
                        return -1;
                    }
                    if (p.ExitCode != 0 && !quiet)
                        Log("Tool failed (" + p.ExitCode + "): " + Path.GetFileName(file) + " " + arguments +
                            " " + (stdout + stderr).Trim());
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                if (!quiet) Log("Tool failed: " + Path.GetFileName(file) + " : " + ex.Message);
                return -1;
            }
        }

        private static void DeleteProtocolKey(string key)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(key + @"\shell\open\command"))
            {
                if (k == null) return;
                string command = Convert.ToString(k.GetValue("", ""));
                if (!ContainsOwnPath(command))
                {
                    Log("Protocol key is not ours, left alone: " + key);
                    return;
                }
            }
            Registry.CurrentUser.DeleteSubKeyTree(key, false);
            Log("Registry key removed: " + key);
        }

        private static bool ContainsOwnPath(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            if (value.IndexOf(_baseDir, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (_root.Length > 0 && value.IndexOf(_root, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // ------------------------------------------------------------------
        //  versions / game files
        // ------------------------------------------------------------------

        // If the package folder is not the share root, seed (and refresh) the
        // share with the game files so every PC uses the same Versions folder.
        private static bool EnsureGameFiles()
        {
            return EnsureGameFiles(null);
        }

        private static bool EnsureGameFiles(Action<string> progress)
        {
            if (_root.Length == 0) return false;
            if (SamePath(_baseDir, _root)) return true;

            FileStream shareLock = AcquireShareLock(300000);
            if (shareLock == null)
            {
                Log("The share is busy (another PC is preparing it). Try again.");
                return false;
            }

            try
            {
                string source = Path.Combine(_baseDir, "Versions");
                string target = Path.Combine(_root, "Versions");

                if (Directory.Exists(source))
                {
                    if (!HasUsableVersion(target))
                    {
                        if (Directory.Exists(target) && Directory.GetFileSystemEntries(target).Length > 0)
                        {
                            string aside = target + ".incomplete-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                            try
                            {
                                Directory.Move(target, aside);
                                Log("Moved incomplete Versions aside: " + aside);
                            }
                            catch (Exception ex)
                            {
                                Log("Could not move the incomplete Versions folder aside: " + ex.Message);
                                return false;
                            }
                        }

                        if (progress != null) progress("Copying Versions to the share...");
                        Log("Copying Versions to the share...");
                        int failed;
                        CopyTree(source, target, progress, out failed);
                        if (failed > 0)
                        {
                            Log("Versions copy failed for " + failed + " item(s); removing the incomplete copy.");
                            try { if (Directory.Exists(target)) Directory.Delete(target, true); } catch { }
                            return false;
                        }
                        Log("Copied Versions.");
                    }
                    else
                    {
                        // The share already has usable versions; Roblox updates
                        // them itself. Never push an older local copy over them.
                        Log("Versions already present on the share; left untouched.");
                    }
                }

                string modsSource = Path.Combine(_baseDir, "Modifications");
                string modsTarget = Path.Combine(_root, "Modifications");
                if (Directory.Exists(modsSource) &&
                    (!Directory.Exists(modsTarget) || Directory.GetFileSystemEntries(modsTarget).Length == 0))
                {
                    if (progress != null) progress("Copying Modifications...");
                    int modFailures;
                    CopyTree(modsSource, modsTarget, progress, out modFailures);
                    if (modFailures > 0) Log("Modifications copy failed for " + modFailures + " item(s).");
                    else Log("Copied Modifications.");
                }
                return true;
            }
            catch (Exception ex)
            {
                Log("Game file copy failed: " + ex.Message);
                return false;
            }
            finally
            {
                try { shareLock.Dispose(); } catch { }
            }
        }

        // Serializes the shared Versions/Modifications seed across PCs.
        private static FileStream AcquireShareLock(int timeoutMs)
        {
            try
            {
                string file = Path.Combine(_root, ".pb_share.lock");
                DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (true)
                {
                    try
                    {
                        return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                            FileShare.None, 1, FileOptions.None);
                    }
                    catch (IOException)
                    {
                        if (DateTime.UtcNow >= end) return null;
                        Thread.Sleep(250);
                    }
                    catch (UnauthorizedAccessException) { return null; }
                }
            }
            catch { return null; }
        }

        private static bool HasUsableVersion(string versionsDir)
        {
            if (!Directory.Exists(versionsDir)) return false;
            try
            {
                foreach (string dir in Directory.GetDirectories(versionsDir, "version-*"))
                {
                    string exe = FindPlayerExe(dir);
                    if (string.IsNullOrEmpty(exe)) continue;
                    try
                    {
                        if (new FileInfo(exe).Length > 0) return true;
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        private static void CopyTree(string source, string target, Action<string> progress, out int failed)
        {
            failed = 0;
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
            {
                try
                {
                    File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
                }
                catch (Exception ex)
                {
                    failed++;
                    Log("Copy failed: " + file + " : " + ex.Message);
                }
            }
            foreach (string dir in Directory.GetDirectories(source))
            {
                if (IsReparsePoint(dir)) continue;
                int sub;
                CopyTree(dir, Path.Combine(target, Path.GetFileName(dir)), progress, out sub);
                failed += sub;
            }
        }

        private static bool FindLatestVersion(out string versionDir, out string exe)
        {
            versionDir = null;
            exe = null;
            DateTime best = DateTime.MinValue;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string shareVersions = Path.Combine(_root, "Versions");
            string baseVersions = Path.Combine(_baseDir, "Versions");

            List<string> roots = new List<string>();
            roots.Add(shareVersions);
            // The share copy is canonical for updates; only look at the local
            // seed when the share has no usable client yet.
            if (!HasUsableVersion(shareVersions)) roots.Add(baseVersions);

            foreach (string versions in roots)
            {
                if (!Directory.Exists(versions)) continue;
                foreach (string dir in Directory.GetDirectories(versions, "version-*"))
                {
                    string folder = Path.GetFileName(dir);
                    if (!seen.Add(folder)) continue;

                    string candidate = FindPlayerExe(dir);
                    if (string.IsNullOrEmpty(candidate)) continue;
                    try
                    {
                        if (new FileInfo(candidate).Length <= 0) continue;
                    }
                    catch { continue; }

                    DateTime stamp;
                    try
                    {
                        // The client EXE's timestamp survives copies; a folder
                        // timestamp does not, so it is only a fallback.
                        stamp = File.GetLastWriteTimeUtc(candidate);
                    }
                    catch { stamp = Directory.GetLastWriteTimeUtc(dir); }

                    Log("Version found: " + Path.GetFileName(dir) + " (" + stamp.ToString("u") + ")");
                    if (stamp > best)
                    {
                        best = stamp;
                        versionDir = dir;
                        exe = candidate;
                    }
                }
            }

            if (!string.IsNullOrEmpty(exe)) return true;

            // Last resort: a flat package that keeps RobloxPlayerBeta.exe at the root.
            foreach (string dir in new string[] { _root, _baseDir })
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                string candidate = FindPlayerExe(dir);
                if (!string.IsNullOrEmpty(candidate))
                {
                    try { if (new FileInfo(candidate).Length <= 0) continue; } catch { continue; }
                    versionDir = dir;
                    exe = candidate;
                    return true;
                }
            }
            return false;
        }

        private static string FindPlayerExe(string dir)
        {
            string[] names = { "RobloxPlayerBeta.exe", "RobloxPlayerLauncher.exe" };
            foreach (string name in names)
            {
                string path = Path.Combine(dir, name);
                if (File.Exists(path)) return path;
            }
            try
            {
                foreach (string file in Directory.GetFiles(dir, "Roblox*.exe"))
                {
                    string name = Path.GetFileName(file).ToLowerInvariant();
                    if (name.Contains("uninstall") || name.Contains("setup")) continue;
                    return file;
                }
            }
            catch { }
            return null;
        }

        // ------------------------------------------------------------------
        //  share config
        // ------------------------------------------------------------------

        private static string ReadShareConfig()
        {
            string file = Path.Combine(_baseDir, "share.cfg");
            try
            {
                if (!File.Exists(file)) return null;
                foreach (string raw in File.ReadAllLines(file))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                    if (line.StartsWith("Root", StringComparison.OrdinalIgnoreCase))
                    {
                        int eq = line.IndexOf('=');
                        if (eq >= 0) return line.Substring(eq + 1).Trim();
                    }
                    else if (Directory.Exists(line))
                    {
                        return line;
                    }
                }
            }
            catch (Exception ex) { Log("Could not read share.cfg: " + ex.Message); }
            return null;
        }

        private static void WriteShareConfig(string root)
        {
            string path = Path.Combine(_baseDir, "share.cfg");
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, "Root=" + root + "\r\n", Encoding.UTF8);
            try
            {
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            catch
            {
                try { File.Copy(temp, path, true); }
                finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
            }
        }

        // ------------------------------------------------------------------
        //  config / helpers
        // ------------------------------------------------------------------

        private static IniFile LoadConfig()
        {
            string external = Path.Combine(_baseDir, "launcher.ini");
            if (File.Exists(external))
            {
                try { return IniFile.Load(external); } catch { }
            }

            string appended = ReadAppendedConfig();
            if (!string.IsNullOrEmpty(appended)) return IniFile.Parse(appended);

            Log("No appended config found; using defaults.");
            IniFile fallback = IniFile.Parse("");
            fallback.Set("App", "Mode", "run");
            fallback.Set("Share", "Root", DefaultRoot);
            return fallback;
        }

        private static string ReadAppendedConfig()
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(Application.ExecutablePath);
                byte[] marker = Encoding.ASCII.GetBytes(AppendedMarker);
                byte[] endm = Encoding.ASCII.GetBytes(AppendedEnd);

                int start = -1;
                for (int i = bytes.Length - marker.Length; i >= 0; i--)
                {
                    if (BytesMatch(bytes, marker, i)) { start = i; break; }
                }
                if (start < 0) return null;

                start += marker.Length;
                int end = -1;
                for (int i = start; i <= bytes.Length - endm.Length; i++)
                {
                    if (BytesMatch(bytes, endm, i)) { end = i; break; }
                }
                if (end < 0) return null;

                string b64 = Encoding.ASCII.GetString(bytes, start, end - start).Trim();
                if (b64.Length == 0) return null;
                return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            }
            catch { return null; }
        }

        private static bool BytesMatch(byte[] haystack, byte[] needle, int at)
        {
            for (int j = 0; j < needle.Length; j++)
                if (haystack[at + j] != needle[j]) return false;
            return true;
        }

        private static string NormalizeRoot(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string root = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')).Replace('/', '\\');
            if (root.Length == 0) return "";

            root = root.TrimEnd('\\');
            if (root.Length == 2 && root[1] == ':') root += "\\";

            try
            {
                if (!Path.IsPathRooted(root)) root = Path.Combine(_baseDir, root);
                root = Path.GetFullPath(root).TrimEnd('\\');
                if (root.Length == 2 && root[1] == ':') root += "\\";
            }
            catch { }
            return root;
        }

        private static bool IsRootAccessible(string root)
        {
            if (string.IsNullOrEmpty(root)) return false;
            try { return Directory.Exists(root); } catch { return false; }
        }

        private static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        // Cheap probe so local data is never moved before we know the link can
        // actually be created (symlinks need admin unless Developer Mode is on).
        private static bool CanCreateSymlinks()
        {
            string dir = "";
            try
            {
                dir = Path.Combine(LocalAppDataPath(), "RobloxPortable", "probe");
                string target = Path.Combine(dir, "target");
                string link = Path.Combine(dir, "link");

                Directory.CreateDirectory(target);
                if (Directory.Exists(link)) Directory.Delete(link, false);
                if (!CreateSymbolicLink(link, target, 1)) return false;
                Directory.Delete(link, false);
                return true;
            }
            catch { return false; }
            finally
            {
                try { if (dir.Length > 0 && Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }
        }

        private static bool RelaunchElevated(string mode, string root, bool noWait)
        {
            return RelaunchElevated(mode, root, noWait, false, "");
        }

        private static bool RelaunchElevated(string mode, string root, bool noWait, bool silent)
        {
            return RelaunchElevated(mode, root, noWait, silent, "");
        }

        private static bool RelaunchElevated(string mode, string root, bool noWait, bool silent, string extra)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.Arguments = "--elevated --mode " + mode +
                                (string.IsNullOrEmpty(root) ? "" : " --root " + Quote(root)) +
                                (noWait ? " --no-wait" : "") +
                                (silent || _silent ? " --silent" : "") +
                                (extra ?? "");
                using (Process child = Process.Start(psi)) { }
                return true;
            }
            catch (Exception ex)
            {
                Log("Elevation failed: " + ex.Message);
                return false;
            }
        }

        private static bool RelaunchElevatedAndWait(string profile)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.Arguments = "--mode prepare --silent --root " + Quote(_root) +
                                " --profile " + Quote(profile ?? "");
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return false;
                    p.WaitForExit();
                    Log("Elevated prepare exit code: " + p.ExitCode);
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                Log("Elevated prepare failed: " + ex.Message);
                return false;
            }
        }

        private static bool AcquireMutex()
        {
            try
            {
                bool created;
                _mutex = new Mutex(true, @"Local\RobloxPortable_" + _device, out created);
                if (!created)
                {
                    _mutex.Dispose();
                    _mutex = null;
                    return false;
                }
                return true;
            }
            catch { return true; }
        }

        private static void ReleaseMutex()
        {
            try
            {
                if (_mutex != null)
                {
                    _mutex.ReleaseMutex();
                    _mutex.Dispose();
                    _mutex = null;
                }
            }
            catch { }
        }

        private static string ProfileRoot()
        {
            if (!string.IsNullOrEmpty(_profileOverride) && Directory.Exists(_profileOverride))
                return _profileOverride;
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        private static string LocalAppDataPath()
        {
            if (!string.IsNullOrEmpty(_profileOverride) && Directory.Exists(_profileOverride))
                return Path.Combine(_profileOverride, "AppData", "Local");
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        private static string RoamingPath()
        {
            if (!string.IsNullOrEmpty(_profileOverride) && Directory.Exists(_profileOverride))
                return Path.Combine(_profileOverride, "AppData", "Roaming");
            return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        private static string LocalLowPath(string localAppData)
        {
            try
            {
                DirectoryInfo parent = Directory.GetParent(localAppData);
                if (parent != null) return Path.Combine(parent.FullName, "LocalLow");
            }
            catch { }
            return Path.Combine(localAppData, "..", "LocalLow");
        }

        private static bool IsReparsePoint(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                return (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch { return false; }
        }

        private static string GetLinkTarget(string path)
        {
            IntPtr handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (handle == new IntPtr(-1)) return null;
            try
            {
                StringBuilder sb = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandle(handle, sb, (uint)sb.Capacity, 0);
                if (length == 0) return null;
                string result = sb.ToString();
                result = NormalizeFinalPath(result);
                return result;
            }
            finally { CloseHandle(handle); }
        }

        private static string NormalizeFinalPath(string result)
        {
            if (string.IsNullOrEmpty(result)) return result;
            if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                return @"\\" + result.Substring(8);
            if (result.StartsWith(@"\\?\", StringComparison.Ordinal))
                return result.Substring(4);
            if (result.StartsWith(@"\??\", StringComparison.Ordinal))
                return result.Substring(4);
            return result;
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(
                    Path.GetFullPath(a).TrimEnd('\\'),
                    Path.GetFullPath(b).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? "") + "\"";
        }

        private static string LogPathForDisplay()
        {
            if (IsRootAccessible(_root))
                return Path.Combine(_root, "Data", "Logs", _device + ".log");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RobloxPortable", "Logs", _device + ".log");
        }

        private static void Log(string message)
        {
            _logBuffer.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message);
        }

        private static void FlushLog()
        {
            if (_logBuffer.Length == 0) return;
            string text = _logBuffer.ToString();

            List<string> candidates = new List<string>();
            if (!string.IsNullOrEmpty(_root))
            {
                try { candidates.Add(Path.Combine(_root, "Data", "Logs", _device + ".log")); }
                catch { }
            }
            try
            {
                candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "RobloxPortable", "Logs", _device + ".log"));
            }
            catch { }

            foreach (string path in candidates)
            {
                try
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.AppendAllText(path, text, Encoding.UTF8);
                    _logBuffer.Length = 0;
                    return;
                }
                catch { }
            }
        }

        private static void Info(string message)
        {
            if (_silent) return;
            MessageBox.Show(message, "Roblox Portable", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void Fail(string message)
        {
            Log("ERROR: " + message);
            FlushLog();
            if (_silent) return;
            MessageBox.Show(message, "Roblox Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ----------------------------------------------------------------------
    //  Minimal INI parser (last value wins)
    // ----------------------------------------------------------------------
    internal sealed class IniFile
    {
        private readonly Dictionary<string, string> _values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static IniFile Load(string path)
        {
            return Parse(File.ReadAllText(path, Encoding.UTF8));
        }

        public static IniFile Parse(string text)
        {
            IniFile ini = new IniFile();
            string section = "";
            string[] lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                ini.Set(section, line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim());
            }
            return ini;
        }

        public void Set(string section, string key, string value)
        {
            _values[(section ?? "") + "|" + (key ?? "")] = value ?? "";
        }

        public string Get(string section, string key, string defaultValue)
        {
            string value;
            return _values.TryGetValue((section ?? "") + "|" + (key ?? ""), out value) ? value : defaultValue;
        }

        public bool GetBool(string section, string key, bool defaultValue)
        {
            string value = Get(section, key, "").Trim().ToLowerInvariant();
            if (value == "1" || value == "true" || value == "yes" || value == "on") return true;
            if (value == "0" || value == "false" || value == "no" || value == "off") return false;
            return defaultValue;
        }
    }

    // ----------------------------------------------------------------------
    //  Share picker
    // ----------------------------------------------------------------------
    internal sealed class ShareForm : Form
    {
        private readonly TextBox _txtRoot;
        private readonly Label _lblStatus;

        public string SelectedRoot { get; private set; }

        public ShareForm(string initialRoot, string device, string appName)
        {
            Text = appName + " - Enable Sharing";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(560, 250);
            Font = new Font("Segoe UI", 9f);

            Label title = new Label();
            title.Text = "Choose the share or folder that every PC can access:";
            title.SetBounds(16, 14, 520, 20);
            Controls.Add(title);

            _txtRoot = new TextBox();
            _txtRoot.Text = initialRoot;
            _txtRoot.SetBounds(16, 40, 430, 24);
            Controls.Add(_txtRoot);

            Button browse = new Button();
            browse.Text = "Browse...";
            browse.SetBounds(452, 39, 90, 26);
            browse.Click += delegate
            {
                using (FolderBrowserDialog dialog = new FolderBrowserDialog())
                {
                    dialog.Description = "Select the shared folder";
                    dialog.SelectedPath = _txtRoot.Text.Trim();
                    dialog.ShowNewFolderButton = true;
                    if (dialog.ShowDialog(this) == DialogResult.OK) _txtRoot.Text = dialog.SelectedPath;
                }
            };
            Controls.Add(browse);

            Label note = new Label();
            note.Text = "Example: \\\\SERVER\\Roblox Portable\r\n" +
                        "Leave the default or type any mapped/UNC share. The share must already exist " +
                        "and allow this Windows account to read and write.";
            note.SetBounds(16, 74, 526, 58);
            Controls.Add(note);

            Label deviceLabel = new Label();
            deviceLabel.Text = "This device: " + device;
            deviceLabel.SetBounds(16, 138, 260, 20);
            Controls.Add(deviceLabel);

            _lblStatus = new Label();
            _lblStatus.Text = "";
            _lblStatus.SetBounds(16, 160, 526, 20);
            _lblStatus.ForeColor = Color.DimGray;
            Controls.Add(_lblStatus);

            Button test = new Button();
            test.Text = "Test";
            test.SetBounds(16, 196, 90, 28);
            test.Click += delegate
            {
                string error;
                if (TestPath(_txtRoot.Text.Trim(), out error))
                {
                    _lblStatus.ForeColor = Color.SeaGreen;
                    _lblStatus.Text = "Accessible and writable.";
                }
                else
                {
                    _lblStatus.ForeColor = Color.Firebrick;
                    _lblStatus.Text = "Not usable: " + error;
                }
            };
            Controls.Add(test);

            Button ok = new Button();
            ok.Text = "OK";
            ok.SetBounds(356, 196, 90, 28);
            ok.DialogResult = DialogResult.OK;
            ok.Click += delegate
            {
                string error;
                if (!TestPath(_txtRoot.Text.Trim(), out error))
                {
                    _lblStatus.ForeColor = Color.Firebrick;
                    _lblStatus.Text = "Not usable: " + error;
                    DialogResult = DialogResult.None;
                    return;
                }
                SelectedRoot = _txtRoot.Text.Trim();
            };
            Controls.Add(ok);

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.SetBounds(452, 196, 90, 28);
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        private static bool TestPath(string root, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(root))
            {
                error = "the path is empty.";
                return false;
            }
            try
            {
                Directory.CreateDirectory(root);
                string probe = Path.Combine(root, ".pb_write_test_" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    // ----------------------------------------------------------------------
    //  Tiny progress window (game file copy)
    // ----------------------------------------------------------------------
    internal sealed class ProgressForm : Form
    {
        private readonly Label _label;

        public ProgressForm(string text)
        {
            Text = "Roblox Portable";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            ClientSize = new Size(460, 96);
            Font = new Font("Segoe UI", 9f);

            _label = new Label();
            _label.Text = text;
            _label.SetBounds(16, 18, 428, 24);
            Controls.Add(_label);

            ProgressBar bar = new ProgressBar();
            bar.Style = ProgressBarStyle.Marquee;
            bar.MarqueeAnimationSpeed = 30;
            bar.SetBounds(16, 50, 428, 18);
            Controls.Add(bar);
        }

        public void SetStatus(string text)
        {
            _label.Text = text;
            Application.DoEvents();
        }
    }
}



