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

namespace FiveMPortable
{
    // FiveM portable launcher (split layout).
    //   run       : links FiveM.app\data to this PC's share folder and starts FiveM.
    //   share     : picks the share, copies the app locally, links the data per PC.
    //   uninstall : removes this PC's data link and optionally its shared data.
    //
    // FiveM keeps everything under %LOCALAPPDATA%\FiveM:
    //   FiveM.exe, FiveM.app\bin, FiveM.app\citizen, mods, plugins  -> local app
    //   FiveM.app\data\cache             (browser/subprocess/extracted shaders)
    //   FiveM.app\data\server-cache      (downloaded resources / maps)
    //   FiveM.app\data\server-cache-priv (private resource cache)
    //   FiveM.app\data\game-storage      (GTA V game data)
    //   FiveM.app\data\nui-storage       (in-game UI storage)
    // Only FiveM.app\data is redirected per PC. The app stays local so FiveM's
    // updater never writes into a folder that other PCs are using.
    internal static class Program
    {
        private static string _baseDir;
        private static string _root;
        private static string _device;
        private static bool _silent;
        private static bool _noWait;
        private static bool _needsElevation;
        private static bool _rootProvided;
        private static string _profileOverride = "";
        private static bool _shareElevatedOnce;
        private static Mutex _mutex;

        private static readonly StringBuilder _logBuffer = new StringBuilder();

        private const string AppendedMarker = ";;PB2CFG;;";
        private const string AppendedEnd = ";;PB2END;;";
        private const string DefaultRoot = @"\\SERVER\FiveM Portable";
        private const string SeedFolder = "FiveM";

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
                bool deleteDataArg = false;

                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i] ?? "";
                    if (string.Equals(a, "--mode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                        modeArg = args[++i];
                    else if (string.Equals(a, "--root", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                        rootArg = args[++i];
                    else if (string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase))
                        _silent = true;
                    else if (string.Equals(a, "--no-wait", StringComparison.OrdinalIgnoreCase))
                        _noWait = true;
                    else if (string.Equals(a, "--delete-data", StringComparison.OrdinalIgnoreCase))
                        deleteDataArg = true;
                    else if (string.Equals(a, "--profile", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                        _profileOverride = args[++i].Trim().Trim('"');
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

                string adminMode = ini.Get("App", "RequireAdmin", "auto").Trim().ToLowerInvariant();
                bool alwaysAdmin = adminMode == "1" || adminMode == "true" ||
                                   adminMode == "yes" || adminMode == "on";
                if (alwaysAdmin && !IsElevated())
                {
                    Log("Administrator rights requested; relaunching with UAC.");
                    FlushLog();
                    if (RelaunchElevated(mode)) return 0;
                    Fail("Administrator rights are required for FiveM Portable.");
                    return 5;
                }

                Log("=== FiveM Portable (" + mode + ") ===");
                Log("Device: " + _device);
                Log("Package: " + _baseDir);
                Log("Share: " + (_root.Length == 0 ? "(not configured)" : _root));

                if (mode == "prepare") return PrepareMode();
                if (mode == "share") return ShareMode(ini);
                if (mode == "uninstall") return UninstallMode(ini, deleteDataArg);
                return RunMode(ini, _noWait);
            }
            catch (Exception ex)
            {
                Log("FATAL: " + ex);
                Fail("FiveM Portable failed.\r\n\r\n" + ex.Message);
                return 1;
            }
            finally
            {
                ReleaseMutex();
                FlushLog();
            }
        }

        // ------------------------------------------------------------------
        //  paths
        // ------------------------------------------------------------------

        private static string LocalFiveM()
        {
            return Path.Combine(LocalAppDataPath(), "FiveM");
        }

        private static string LocalData()
        {
            return Path.Combine(LocalFiveM(), "FiveM.app", "data");
        }

        // Per-PC data folder on the share. Only this folder is linked; the app
        // itself stays on the local disk.
        private static string TargetData(string root)
        {
            return Path.Combine(root, "Data", "Local", _device, "FiveM", "data");
        }

        private static string SeedPath()
        {
            return Path.Combine(_baseDir, SeedFolder);
        }

        // ------------------------------------------------------------------
        //  prepare (elevated, links only)
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
                Info("FiveM Portable is already running on this PC.");
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
                    Log("Elevation required for the link; preparing it with admin.");
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
                        Fail("Administrator rights are required to create the FiveM link.");
                        return 5;
                    }
                }
                else
                {
                    Fail(error);
                    return 8;
                }
            }

            string exe = Path.Combine(LocalFiveM(), "FiveM.exe");
            if (!File.Exists(exe))
            {
                Fail("FiveM.exe not found.\r\n\r\nExpected:\r\n" + exe +
                     "\r\n\r\nRun Enable_Sharing.exe once so the app is copied locally.");
                return 9;
            }

            string arguments = BuildExtraArguments();
            Log("Launching: " + exe + (arguments.Length == 0 ? "" : " " + arguments));

            ProcessStartInfo psi = new ProcessStartInfo(exe);
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            psi.UseShellExecute = true;
            if (arguments.Length > 0) psi.Arguments = arguments;

            using (Process game = Process.Start(psi))
            {
                if (game == null)
                {
                    Fail("Could not start FiveM.");
                    return 10;
                }
                if (noWait) return 0;

                Log("Waiting for FiveM to exit...");
                FlushLog();
                game.WaitForExit();
                Log("FiveM exited with code " + game.ExitCode + ".");
            }
            return 0;
        }

        private static string BuildExtraArguments()
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
                Info("FiveM Portable is already running on this PC.");
                return 0;
            }

            bool ok = ShareModeInteractive(ini);
            if (!ok) return 7;

            if (_needsElevation && !IsElevated())
            {
                if (_shareElevatedOnce)
                {
                    Fail("The FiveM link still could not be created. Check the share permissions.");
                    return 5;
                }
                _shareElevatedOnce = true;

                Log("Elevation required for the link; preparing it with admin.");
                FlushLog();
                if (!RelaunchElevatedAndWait(ProfileRoot()))
                {
                    Fail("Administrator rights are required to create the FiveM link.");
                    return 5;
                }

                _needsElevation = false;
                _rootProvided = true;
                if (!ShareModeInteractive(ini)) return 7;
                if (_needsElevation)
                {
                    Fail("The FiveM link could not be created after elevation.");
                    return 5;
                }
            }
            return 0;
        }

        private static bool ShareModeInteractive(IniFile ini)
        {
            string root = _root;
            if (root.Length == 0) root = DefaultRoot;

            if (!_silent && !_rootProvided)
            {
                using (ShareForm form = new ShareForm(root, _device, ini.Get("App", "Name", "FiveM Portable")))
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

            string error;
            bool linksReady = PrepareDevice(root, out error);
            if (!linksReady)
            {
                if (_needsElevation)
                {
                    Log("Link needs elevation: " + error);
                    return true;
                }
                Fail(error);
                return false;
            }
            Log("Device linked: " + _device);

            if (!_silent)
            {
                string info = "FiveM sharing is ready.\r\n\r\n" +
                              "Share: " + root + "\r\n" +
                              "Device: " + _device + "\r\n" +
                              "App (local): " + LocalFiveM() + "\r\n" +
                              "Data (share): " + TargetData(root) + "\r\n\r\n" +
                              "Run FiveM.exe on this PC to play.\r\n" +
                              "Other PCs run Enable_Sharing.exe once, then FiveM.exe.\r\n\r\n" +
                              "The app stays local; only FiveM.app\\data is on the share,\r\n" +
                              "so resources, maps and shaders stay isolated per PC.\r\n\r\n" +
                              "Log: " + LogPathForDisplay();
                MessageBox.Show(info, "FiveM Portable - Enable Sharing",
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

        private static bool PrepareDevice(string root, out string error)
        {
            error = null;
            try
            {
                string local = LocalFiveM();
                string localData = LocalData();
                string targetData = TargetData(root);
                string seed = SeedPath();

                string logs = Path.Combine(root, "Data", "Logs");
                Directory.CreateDirectory(targetData);
                Directory.CreateDirectory(logs);

                // Migration from the previous whole-folder layout: %LOCALAPPDATA%\FiveM
                // used to be a link to <share>\Data\Local\<PC>\FiveM. Restore the app
                // locally and keep only FiveM.app\data on the share.
                if (IsReparsePoint(local))
                {
                    if (!MigrateWholeFolderLink(local, targetData, out error)) return false;
                }

                if (File.Exists(local))
                {
                    error = "A file occupies the FiveM path:\r\n" + local;
                    return false;
                }

                // Refuse before touching local data if the link cannot be created
                // (standard user who has not approved UAC yet).
                bool dataLinked = IsReparsePoint(localData) && SamePath(GetLinkTarget(localData), targetData);
                if (!dataLinked && !IsElevated() && !CanCreateSymlinks())
                {
                    _needsElevation = true;
                    error = "Administrator rights are required to create the FiveM link.";
                    return false;
                }

                if (!EnsureLocalApp(seed, out error)) return false;
                if (!EnsureDataLink(targetData, out error)) return false;
                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                _needsElevation = true;
                error = "Access denied while preparing FiveM:\r\n" + ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = "Preparing FiveM failed:\r\n" + ex.Message;
                return false;
            }
        }

        private static bool EnsureLocalApp(string seed, out string error)
        {
            error = null;
            string local = LocalFiveM();
            string exe = Path.Combine(local, "FiveM.exe");
            string app = Path.Combine(local, "FiveM.app");

            if (!File.Exists(exe) || !Directory.Exists(app))
            {
                if (!Directory.Exists(seed))
                {
                    error = "FiveM files not found.\r\n\r\nExpected the app seed at:\r\n" + seed +
                            "\r\nor an existing local FiveM install at:\r\n" + local;
                    return false;
                }

                Log("Copying the FiveM app locally: " + seed + " -> " + local);
                Directory.CreateDirectory(local);
                int failed;
                CopyTree(seed, local, null, out failed);
                if (failed > 0)
                {
                    error = "Copying the FiveM app failed for " + failed + " item(s).\r\n" +
                            "Close FiveM and run Enable_Sharing.exe again.";
                    return false;
                }
            }

            if (!File.Exists(exe) || !Directory.Exists(app))
            {
                error = "FiveM.exe / FiveM.app were not found locally:\r\n" + local +
                        "\r\n\r\nPlace a valid FiveM package next to the launcher and try again.";
                return false;
            }
            return true;
        }

        private static bool EnsureDataLink(string targetData, out string error)
        {
            error = null;
            string local = LocalData();

            if (IsReparsePoint(local))
            {
                string actual = GetLinkTarget(local);
                if (SamePath(actual, targetData))
                {
                    Log("Link ok: " + local + " -> " + targetData);
                    return true;
                }

                if (!IsElevated() && !CanCreateSymlinks())
                {
                    _needsElevation = true;
                    error = "Administrator rights are required to create the FiveM data link.";
                    return false;
                }

                if (!string.IsNullOrEmpty(actual) && Directory.Exists(actual))
                {
                    Log("Migrating FiveM data from the previous share: " + actual);
                    if (!CopyMissing(actual, targetData, 0))
                    {
                        error = "Could not copy the old FiveM data to the new share.\r\n" +
                                "The previous link was left in place. Close FiveM and try again.";
                        return false;
                    }
                }

                Log("Replacing data link: " + local + " (was " + (actual ?? "unreadable") + ")");
                if (!TryDeleteLink(local))
                {
                    error = "Could not replace the existing FiveM data link:\r\n" + local;
                    return false;
                }
            }
            else if (Directory.Exists(local))
            {
                if (!IsElevated() && !CanCreateSymlinks())
                {
                    _needsElevation = true;
                    error = "Administrator rights are required to create the FiveM data link.";
                    return false;
                }

                Log("Adopting the local FiveM.app\\data into the share: " + local);
                if (!MergeMove(local, targetData, 0))
                {
                    error = "Could not move %LOCALAPPDATA%\\FiveM\\FiveM.app\\data to the share.\r\n" +
                            "Close FiveM (and FiveM_GTAProcess) and try again.";
                    return false;
                }
            }
            else if (File.Exists(local))
            {
                error = "A file occupies the FiveM data path:\r\n" + local;
                return false;
            }
            else if (!IsElevated() && !CanCreateSymlinks())
            {
                _needsElevation = true;
                error = "Administrator rights are required to create the FiveM data link.";
                return false;
            }

            Directory.CreateDirectory(targetData);
            string appDir = Path.GetDirectoryName(local);
            if (!string.IsNullOrEmpty(appDir)) Directory.CreateDirectory(appDir);

            if (!CreateSymbolicLink(local, targetData, 1))
            {
                int code = Marshal.GetLastWin32Error();
                if (code == 1314) _needsElevation = true;
                error = "Creating the FiveM data link failed (Win32 " + code + "):\r\n" + local +
                        "\r\n-> " + targetData +
                        (code == 1314 ? "\r\n\r\nAdministrator rights are required." : "");
                Log("mklink failed (" + code + "): " + local + " -> " + targetData);
                return false;
            }

            Log("Link created: " + local + " -> " + targetData);
            return true;
        }

        // Converts the old whole-folder link into: local app + per-PC data link.
        private static bool MigrateWholeFolderLink(string local, string targetData, out string error)
        {
            error = null;
            string actual = GetLinkTarget(local);
            if (string.IsNullOrEmpty(actual) || !Directory.Exists(actual))
            {
                error = "The existing %LOCALAPPDATA%\\FiveM link is unreadable:\r\n" + local;
                return false;
            }

            string actualExe = Path.Combine(actual, "FiveM.exe");
            string actualApp = Path.Combine(actual, "FiveM.app");
            if (!File.Exists(actualExe) && !Directory.Exists(actualApp))
            {
                error = "The existing %LOCALAPPDATA%\\FiveM link does not point at a FiveM folder:\r\n" +
                        local + "\r\n-> " + actual;
                return false;
            }

            if (SamePath(actual, targetData))
            {
                error = "The existing FiveM link points at the data folder instead of the app:\r\n" + local;
                return false;
            }

            Log("Migrating the previous whole-folder FiveM link to the split layout...");
            Log("Old share folder: " + actual);

            string stage = local + ".pb-stage-" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.CreateDirectory(stage);

                foreach (string file in Directory.GetFiles(actual))
                {
                    MoveFile(file, Path.Combine(stage, Path.GetFileName(file)));
                }

                foreach (string dir in Directory.GetDirectories(actual))
                {
                    string name = Path.GetFileName(dir);
                    if (string.Equals(name, "FiveM.app", StringComparison.OrdinalIgnoreCase))
                    {
                        string appStage = Path.Combine(stage, "FiveM.app");
                        Directory.CreateDirectory(appStage);

                        foreach (string file in Directory.GetFiles(dir))
                            MoveFile(file, Path.Combine(appStage, Path.GetFileName(file)));

                        foreach (string sub in Directory.GetDirectories(dir))
                        {
                            string subName = Path.GetFileName(sub);
                            if (string.Equals(subName, "data", StringComparison.OrdinalIgnoreCase))
                            {
                                Log("Adopting the previous per-PC data: " + sub);
                                if (!MergeMove(sub, targetData, 0))
                                {
                                    error = "Could not move the old FiveM.app\\data to the share.";
                                    return false;
                                }
                                continue;
                            }
                            MoveDirectory(sub, Path.Combine(appStage, subName));
                        }
                    }
                    else if (SamePath(dir, targetData))
                    {
                        // Placeholder created just before the migration; it is
                        // recreated by EnsureDataLink and must not go local.
                        continue;
                    }
                    else
                    {
                        MoveDirectory(dir, Path.Combine(stage, name));
                    }
                }
            }
            catch (Exception ex)
            {
                error = "Migrating the previous FiveM layout failed:\r\n" + ex.Message;
                return false;
            }

            if (!TryDeleteLink(local))
            {
                error = "Could not remove the old FiveM link:\r\n" + local;
                return false;
            }

            try
            {
                Directory.Move(stage, local);
            }
            catch (Exception ex)
            {
                error = "Could not restore the FiveM app locally:\r\n" + ex.Message;
                return false;
            }

            TryDeleteEmptyTree(actual);

            Log("FiveM app restored locally; data kept on the share.");
            return true;
        }

        // ------------------------------------------------------------------
        //  uninstall
        // ------------------------------------------------------------------

        private static int UninstallMode(IniFile ini, bool deleteDataArg)
        {
            if (!AcquireMutex())
            {
                Log("Another FiveM Portable action is already running.");
                return 0;
            }

            bool deleteData = deleteDataArg;
            bool confirmed = _silent;
            if (!_silent)
            {
                string text = "Remove this PC's FiveM Portable data link?\r\n\r\n" +
                              "Device: " + _device + "\r\n" +
                              "Share: " + (_root.Length == 0 ? "(not configured)" : _root) + "\r\n\r\n" +
                              "The local FiveM app is kept. Only the link to this PC's shared data is removed." +
                              "\r\n\r\nDelete this device's shared FiveM data as well?";
                DialogResult answer = MessageBox.Show(text, "FiveM Portable - Uninstall",
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button3);
                if (answer == DialogResult.Cancel) { Log("Uninstall cancelled."); return 0; }
                confirmed = true;
                deleteData = (answer == DialogResult.Yes);
            }
            if (!confirmed) return 0;

            string local = LocalFiveM();
            string localData = LocalData();

            if (IsReparsePoint(local))
            {
                // Previous whole-folder layout.
                string target = GetLinkTarget(local);
                if (!string.IsNullOrEmpty(target) && IsShareOwned(target))
                {
                    if (TryDeleteLink(local)) Log("Removed old whole-folder link: " + local);
                    else Log("Could not remove link: " + local);
                }
                else
                {
                    Log("Link is not ours, left alone: " + local + " -> " + (target ?? "unreadable"));
                }
            }
            else if (IsReparsePoint(localData))
            {
                string target = GetLinkTarget(localData);
                if (!string.IsNullOrEmpty(target) && IsShareOwned(target))
                {
                    if (TryDeleteLink(localData)) Log("Removed data link: " + localData);
                    else Log("Could not remove data link: " + localData);
                }
                else
                {
                    Log("Data link is not ours, left alone: " + localData + " -> " + (target ?? "unreadable"));
                }
            }
            else
            {
                Log("Not a link, left alone: " + localData);
            }

            if (deleteData)
            {
                if (IsReparsePoint(local) || IsReparsePoint(localData))
                {
                    Log("Shared data was not deleted because a link is still present.");
                }
                else if (!string.IsNullOrEmpty(_root))
                {
                    string dir = Path.Combine(_root, "Data", "Local", _device);
                    try
                    {
                        if (Directory.Exists(dir))
                        {
                            Directory.Delete(dir, true);
                            Log("Deleted shared FiveM data: " + dir);
                        }
                    }
                    catch (Exception ex) { Log("Could not delete " + dir + ": " + ex.Message); }
                }
            }

            if (!_silent)
            {
                MessageBox.Show("FiveM Portable was removed.\r\n\r\n" +
                    "The data link was removed. The local FiveM app was kept, and the shared data was " +
                    "kept unless you chose to delete it.",
                    "FiveM Portable - Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return 0;
        }

        private static bool IsShareOwned(string path)
        {
            string p = (path ?? "").Replace('/', '\\');
            if (string.IsNullOrEmpty(_root) || !IsUnderRootPath(p, _root)) return false;
            return p.IndexOf(@"\Data\Local\", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   p.IndexOf(@"\FiveM", StringComparison.OrdinalIgnoreCase) >= 0;
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

        // ------------------------------------------------------------------
        //  links / copy helpers
        // ------------------------------------------------------------------

        private static void MoveFile(string source, string target)
        {
            try { File.Move(source, target); }
            catch (IOException)
            {
                File.Copy(source, target, false);
                File.Delete(source);
            }
        }

        // Directory.Move cannot cross volumes (local disk <-> share), so copy+delete.
        private static void MoveDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
                MoveFile(file, Path.Combine(target, Path.GetFileName(file)));
            foreach (string dir in Directory.GetDirectories(source))
            {
                if (IsReparsePoint(dir)) continue;
                MoveDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
            }
            Directory.Delete(source, false);
        }

        private static void TryDeleteEmptyTree(string path)
        {
            try
            {
                if (!Directory.Exists(path) || IsReparsePoint(path)) return;
                foreach (string dir in Directory.GetDirectories(path)) TryDeleteEmptyTree(dir);
                bool hasFiles = false;
                foreach (string file in Directory.GetFiles(path)) { hasFiles = true; break; }
                if (!hasFiles && Directory.GetFileSystemEntries(path).Length == 0)
                    Directory.Delete(path, false);
            }
            catch { }
        }

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
                    try { File.Move(file, dest); }
                    catch (IOException)
                    {
                        try { File.Copy(file, dest, false); File.Delete(file); }
                        catch { return false; }
                    }
                }
                foreach (string dir in Directory.GetDirectories(source))
                {
                    if (IsReparsePoint(dir)) continue;
                    if (!MergeMove(dir, Path.Combine(target, Path.GetFileName(dir)), depth + 1)) return false;
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

        private static void CopyTree(string source, string target, Action<string> progress, out int failed)
        {
            failed = 0;
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
            {
                try { File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true); }
                catch (Exception ex) { failed++; Log("Copy failed: " + file + " : " + ex.Message); }
            }
            foreach (string dir in Directory.GetDirectories(source))
            {
                if (IsReparsePoint(dir)) continue;
                int sub;
                CopyTree(dir, Path.Combine(target, Path.GetFileName(dir)), progress, out sub);
                failed += sub;
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
            // GetFinalPathNameByHandle returns \\?\UNC\server\share for UNC
            // links. Naively stripping \\?\ leaves "UNC\server\..." which never
            // matches the configured share and breaks link ownership checks.
            if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                return @"\\" + result.Substring(8);
            if (result.StartsWith(@"\\?\", StringComparison.Ordinal))
                return result.Substring(4);
            if (result.StartsWith(@"\??\", StringComparison.Ordinal))
                return result.Substring(4);
            return result;
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

        private static bool IsRootAccessible(string root)
        {
            if (string.IsNullOrEmpty(root)) return false;
            try { return Directory.Exists(root); } catch { return false; }
        }

        // ------------------------------------------------------------------
        //  config / elevation / logging
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

            IniFile fallback = IniFile.Parse("");
            fallback.Set("App", "Mode", "run");
            fallback.Set("App", "RequireAdmin", "auto");
            return fallback;
        }

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

        private static string ReadAppendedConfig()
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(Application.ExecutablePath);
                byte[] marker = Encoding.ASCII.GetBytes(AppendedMarker);
                byte[] endm = Encoding.ASCII.GetBytes(AppendedEnd);

                int start = -1;
                for (int i = bytes.Length - marker.Length; i >= 0; i--)
                    if (BytesMatch(bytes, marker, i)) { start = i; break; }
                if (start < 0) return null;
                start += marker.Length;

                int end = -1;
                for (int i = start; i <= bytes.Length - endm.Length; i++)
                    if (BytesMatch(bytes, endm, i)) { end = i; break; }
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

        private static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static bool CanCreateSymlinks()
        {
            string dir = "";
            try
            {
                dir = Path.Combine(LocalAppDataPath(), "FiveMPortable", "probe");
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

        private static bool RelaunchElevated(string mode)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.Arguments = "--elevated --mode " + mode +
                                (_noWait ? " --no-wait" : "") +
                                (_silent ? " --silent" : "");
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
                _mutex = new Mutex(true, @"Local\FiveMPortable_" + _device, out created);
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

        private static string Quote(string value)
        {
            return "\"" + (value ?? "") + "\"";
        }

        private static string LogPathForDisplay()
        {
            if (IsRootAccessible(_root))
                return Path.Combine(_root, "Data", "Logs", _device + ".log");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FiveMPortable", "Logs", _device + ".log");
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
                    "FiveMPortable", "Logs", _device + ".log"));
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
            MessageBox.Show(message, "FiveM Portable", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void Fail(string message)
        {
            Log("ERROR: " + message);
            FlushLog();
            if (_silent) return;
            MessageBox.Show(message, "FiveM Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

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
            note.Text = "Example: \\\\SERVER\\FiveM Portable\r\n" +
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
}
