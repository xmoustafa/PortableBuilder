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

namespace GameLoopPortable
{
    // GameLoop / TxGameAssistant portable launcher.
    //   run    : starts <root>\UI\AndroidEmulatorEn.exe with the PUBG Mobile args
    //   apply  : imports the exported .reg files, fixes every path to <root>,
    //            links Snapshot + Temp into the package (admin)
    //   remove : stops the emulator, removes our links and restores/removes the
    //            registry values captured before the first apply (admin)
    internal static class Program
    {
        private static string _baseDir;
        private static string _root;
        private static string _device;
        private static bool _silent;
        private static bool _dryRun;
        private static bool _noWait;
        private static bool _noElevate;
        private static Mutex _mutex;

        private static readonly StringBuilder _logBuffer = new StringBuilder();

        private const string AppendedMarker = ";;PB2CFG;;";
        private const string AppendedEnd = ";;PB2END;;";
        private const string GameArgs = "-vm 100 -engine aow -startpkg com.tencent.ig";

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
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i] ?? "";
                    if (string.Equals(a, "--mode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                        modeArg = args[++i];
                    else if (string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase))
                        _silent = true;
                    else if (string.Equals(a, "--no-wait", StringComparison.OrdinalIgnoreCase))
                        _noWait = true;
                    else if (string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase))
                        _dryRun = true;
                    else if (string.Equals(a, "--no-elevate", StringComparison.OrdinalIgnoreCase))
                        _noElevate = true;
                }

                IniFile ini = LoadConfig();
                _silent = _silent || ini.GetBool("App", "Silent", false);

                string mode = (modeArg ?? ini.Get("App", "Mode", "run")).Trim().ToLowerInvariant();
                _root = NormalizeRoot(ini.Get("App", "Root", ""));
                if (_root.Length == 0) _root = _baseDir;

                if (_root.StartsWith(@"\\", StringComparison.Ordinal) || IsNetworkPath(_root))
                {
                    Fail("GameLoop Portable must run from a local folder (for example D:\\TxGameAssistant).\r\n" +
                         "UNC paths and mapped network drives would share Snapshot and Temp between PCs.\r\n" +
                         "Copy the package to D:\\TxGameAssistant on each PC.");
                    return 9;
                }

                string adminMode = ini.Get("App", "RequireAdmin", "auto").Trim().ToLowerInvariant();
                bool alwaysAdmin = adminMode == "1" || adminMode == "true" ||
                                   adminMode == "yes" || adminMode == "on";
                if (alwaysAdmin && !_noElevate && !IsElevated())
                {
                    Log("Administrator rights requested; relaunching with UAC.");
                    FlushLog();
                    if (RelaunchElevated(mode)) return 0;
                    Fail("Administrator rights are required for GameLoop Portable.");
                    return 5;
                }

                Log("=== GameLoop Portable (" + mode + ") ===");
                Log("Device: " + _device);
                Log("Root: " + _root);

                if (mode == "apply") return ApplyMode();
                if (mode == "remove") return RemoveMode();
                return RunMode();
            }
            catch (Exception ex)
            {
                Log("FATAL: " + ex);
                Fail("GameLoop Portable failed.\r\n\r\n" + ex.Message);
                return 1;
            }
            finally
            {
                ReleaseMutex();
                FlushLog();
            }
        }

        // ------------------------------------------------------------------
        //  run
        // ------------------------------------------------------------------

        private static int RunMode()
        {
            string exe = FindEmulator();
            if (string.IsNullOrEmpty(exe))
            {
                Fail("GameLoop emulator not found.\r\n\r\nExpected:\r\n" +
                     Path.Combine(_root, "UI", "AndroidEmulatorEn.exe") +
                     "\r\n\r\nRun Apply Gameloop.exe once, then try again.");
                return 7;
            }

            string arguments = GameArgs;
            string extra = BuildExtraArguments();
            if (extra.Length > 0) arguments += " " + extra;

            Log("Launching: " + exe + " " + arguments);
            ProcessStartInfo psi = new ProcessStartInfo(exe, arguments);
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            psi.UseShellExecute = true;

            using (Process game = Process.Start(psi))
            {
                if (game == null)
                {
                    Fail("Could not start GameLoop.");
                    return 8;
                }

                if (_noWait) return 0;

                Log("Waiting for the emulator to exit...");
                FlushLog();
                game.WaitForExit();
                Log("Emulator exited with code " + game.ExitCode + ".");
            }
            return 0;
        }

        private static string FindEmulator()
        {
            string[] dirs = { "UI", "ui", "Ui" };
            foreach (string dir in dirs)
            {
                string path = Path.Combine(_root, dir, "AndroidEmulatorEn.exe");
                if (File.Exists(path)) return path;
            }
            return null;
        }

        // The download/temp link must live on a local drive. If the package is
        // on a UNC path, Path.GetPathRoot returns the share root instead.
        private static string TempLinkPath()
        {
            string drive = Path.GetPathRoot(_root);
            if (string.IsNullOrEmpty(drive) ||
                drive.StartsWith(@"\\", StringComparison.Ordinal))
            {
                drive = Environment.GetEnvironmentVariable("SystemDrive");
                if (string.IsNullOrEmpty(drive)) drive = "C:";
            }
            if (!drive.EndsWith("\\", StringComparison.Ordinal)) drive += "\\";
            return Path.Combine(drive, "Temp", "TxGameDownload");
        }

        private static string BuildExtraArguments()
        {
            string[] args = Environment.GetCommandLineArgs();
            StringBuilder sb = new StringBuilder();
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i] ?? "";
                if (string.Equals(a, "--mode", StringComparison.OrdinalIgnoreCase))
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
        //  apply
        // ------------------------------------------------------------------

        private static int ApplyMode()
        {
            if (!AcquireMutex())
            {
                Log("Another apply/remove is already running.");
                return 0;
            }

            if (!ValidateRoot()) return 9;

            Log((_dryRun ? "[dry-run] " : "") + "Stopping GameLoop processes and services...");
            if (!_dryRun) StopGameLoop();

            try
            {
            string snapshot = Path.Combine(_root, "Snapshot");
            string temp = Path.Combine(_root, "Temp");
            if (_dryRun)
            {
                Log("[dry-run] create " + snapshot);
                Log("[dry-run] create " + temp);
            }
            else
            {
                Directory.CreateDirectory(snapshot);
                Directory.CreateDirectory(Path.Combine(temp, "Component"));
                Directory.CreateDirectory(Path.Combine(temp, "MobileGamePCShared"));
            }

            string snapshotLink = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Tencent", "TxGameAssistant", "Snapshot");
            string tempLink = TempLinkPath();

            Log("Snapshot link: " + snapshotLink + " -> " + snapshot);
            Log("Temp link:     " + tempLink + " -> " + temp);

            if (!_dryRun)
            {
                // Back up first: if the registry cannot be captured, do not
                // leave Snapshot/Temp redirected in a half-applied state.
                if (!BackupRegistryOnce())
                {
                    Fail("Could not back up the GameLoop registry keys.\r\n\r\n" +
                         "Nothing was imported or changed.");
                    return 11;
                }
            }

            if (!_dryRun)
            {
                string error;
                if (!EnsureLink(snapshotLink, snapshot, out error)) { Fail(error); return 10; }
                if (!EnsureLink(tempLink, temp, out error)) { Fail(error); return 10; }
            }

            if (!_dryRun)
            {
                ImportRegistryFiles();
                PatchRegistry(snapshotLink, tempLink);
            }
            else
            {
                Log("[dry-run] would back up + import Registry\\*.reg and patch paths.");
            }

            Log("Apply finished.");
            FlushLog();
            if (!_silent)
            {
                MessageBox.Show("GameLoop is ready.\r\n\r\n" +
                    "Root: " + _root + "\r\n" +
                    "Snapshot: " + snapshotLink + "\r\n" +
                    "Temp: " + tempLink + "\r\n\r\n" +
                    "Run GameLoop.exe or PUBG Mobile.exe to play.",
                    "GameLoop Portable - Apply", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
                return 0;
            }
            finally
            {
                if (!_dryRun)
                {
                    // The apply stops the emulator services to unlock files;
                    // start them again best-effort so the game can run.
                    StartService("aow_drv");
                    StartService("QMEmulatorService");
                }
            }
        }

        private static bool ValidateRoot()
        {
            if (!Directory.Exists(_root))
            {
                Fail("GameLoop folder not found:\r\n" + _root);
                return false;
            }
            if (FindEmulator() == null)
            {
                Fail("This folder does not look like a GameLoop installation.\r\n\r\n" +
                     "Missing UI\\AndroidEmulatorEn.exe or AppMarket\\AppMarket.exe under:\r\n" + _root);
                return false;
            }
            return true;
        }

        private static void StopGameLoop()
        {
            StopService("aow_drv");
            StopService("QMEmulatorService");

            string[] names =
            {
                "AndroidEmulator", "AndroidEmulatorEn", "AndroidEmulatorEx", "AndroidEmulatorRender",
                "AndroidEmulatorHeadless", "AndroidProcess", "QMProxy", "AndroidService",
                "syzs_dl_svr", "GameLoop", "AppMarket", "GameLoader", "aow_exe", "adb",
                "QMEmulatorService", "TSettingCenter", "TxGameAssistant"
            };
            foreach (string name in names)
            {
                try
                {
                    foreach (Process p in Process.GetProcessesByName(name))
                    {
                        try
                        {
                            // "adb" is a shared tool name; only stop the copy that
                            // belongs to this GameLoop package. Otherwise an
                            // unrelated Android Studio/phone-tool adb would die.
                            if (string.Equals(name, "adb", StringComparison.OrdinalIgnoreCase) &&
                                !IsProcessUnderRoot(p))
                            {
                                Log("Skipped foreign adb process: " + p.Id);
                                continue;
                            }
                            p.Kill();
                            Log("Killed: " + name + " (" + p.Id + ")");
                        }
                        catch { }
                        finally { try { p.Dispose(); } catch { } }
                    }
                }
                catch { }
            }
            Thread.Sleep(500);
        }

        private static bool IsProcessUnderRoot(Process process)
        {
            try
            {
                string file = process.MainModule == null ? null : process.MainModule.FileName;
                return !string.IsNullOrEmpty(file) && IsUnderRoot(file);
            }
            catch { return false; }
        }

        private static void StopService(string name)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(
                    Path.Combine(Environment.SystemDirectory, "sc.exe"), "stop \"" + name + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    if (p != null)
                    {
                        try { p.WaitForExit(4000); } catch { }
                    }
                }
                Log("Service stop requested: " + name);
            }
            catch (Exception ex) { Log("Service stop failed: " + name + " : " + ex.Message); }
        }

        private static void StartService(string name)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(
                    Path.Combine(Environment.SystemDirectory, "sc.exe"), "start \"" + name + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return;
                    p.WaitForExit(4000);
                    Log("Service start requested: " + name + " (exit " +
                        (p.HasExited ? p.ExitCode.ToString() : "timeout") + ")");
                }
            }
            catch (Exception ex) { Log("Service start failed: " + name + " : " + ex.Message); }
        }

        // ------------------------------------------------------------------
        //  registry backup / import / patch
        // ------------------------------------------------------------------

        private static readonly string[] ManagedKeys =
        {
            @"HKLM\SOFTWARE\WOW6432Node\Tencent\MobileGamePC",
            @"HKCU\SOFTWARE\Tencent\MobileGamePC",
            @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\MobileGamePC"
        };

        private static bool BackupRegistryOnce()
        {
            try
            {
                string dir = Path.Combine(_root, "Registry", "backup");
                string state = Path.Combine(dir, "state.txt");
                if (File.Exists(state))
                {
                    Log("Registry backup already exists.");
                    return BackupIsComplete(dir, state);
                }

                Directory.CreateDirectory(dir);
                string reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
                StringBuilder sb = new StringBuilder();

                foreach (string key in ManagedKeys)
                {
                    string file = Path.Combine(dir, BackupFileName(key));
                    string args = "export " + Quote(key) + " " + Quote(file) + " /y";
                    int code = RunTool(reg, args, true);
                    if (code == 0) sb.AppendLine(key + "|present");
                    else
                    {
                        sb.AppendLine(key + "|absent");
                        try { if (File.Exists(file)) File.Delete(file); } catch { }
                    }
                }

                File.WriteAllText(state, sb.ToString(), Encoding.UTF8);
                Log("Registry backup saved: " + dir);
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
                    string key = line.Substring(0, bar);
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

        private static void ImportRegistryFiles()
        {
            string dir = Path.Combine(_root, "Registry");
            string reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
            string[] files = { "Tencent.reg", "Tencent2.reg", "Uninstall.reg" };

            foreach (string name in files)
            {
                string file = Path.Combine(dir, name);
                if (!File.Exists(file)) { Log("Registry file missing (skipped): " + name); continue; }

                string reason;
                if (!ValidateRegFile(file, out reason))
                {
                    Log("Registry file rejected (" + name + "): " + reason);
                    continue;
                }

                int code = RunTool(reg, "import " + Quote(file), false);
                Log("reg import (exit " + code + "): " + name);
            }
        }

        // Only the GameLoop/Tencent keys the package owns may be imported.
        // This prevents a tampered Registry\*.reg from writing arbitrary HKLM
        // entries while running elevated.
        private static bool ValidateRegFile(string file, out string reason)
        {
            reason = null;
            string[] allowed =
            {
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Tencent\MobileGamePC",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Tencent\MobileGamePC",
                @"HKEY_CURRENT_USER\SOFTWARE\Tencent\MobileGamePC",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\MobileGamePC"
            };

            try
            {
                string[] lines = File.ReadAllLines(file);
                if (lines.Length == 0) { reason = "empty file"; return false; }

                string header = "";
                for (int i = 0; i < lines.Length; i++)
                {
                    string l = (lines[i] ?? "").Trim();
                    if (l.Length == 0) continue;
                    header = l;
                    break;
                }
                if (header.IndexOf("Windows Registry Editor", StringComparison.OrdinalIgnoreCase) < 0 &&
                    !string.Equals(header, "REGEDIT4", StringComparison.OrdinalIgnoreCase))
                {
                    reason = "not a .reg file";
                    return false;
                }

                int sections = 0;
                foreach (string raw in lines)
                {
                    string line = (raw ?? "").Trim();
                    if (line.Length < 3 || line[0] != '[' || line[line.Length - 1] != ']') continue;

                    string key = line.Substring(1, line.Length - 2).Trim();
                    if (key.StartsWith("-", StringComparison.Ordinal)) key = key.Substring(1).Trim();
                    key = key.Replace('/', '\\');
                    sections++;

                    bool ok = false;
                    foreach (string prefix in allowed)
                    {
                        if (string.Equals(key, prefix, StringComparison.OrdinalIgnoreCase) ||
                            key.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
                        {
                            ok = true;
                            break;
                        }
                    }
                    if (!ok)
                    {
                        reason = "key outside the GameLoop scope: " + key;
                        return false;
                    }
                }

                if (sections == 0) { reason = "no registry keys"; return false; }
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        private static void PatchRegistry(string snapshotLink, string tempLink)
        {
            string install = _root;
            string tempTarget = Path.Combine(tempLink, "Component") + "\\";
            string shared = Path.Combine(tempLink, "MobileGamePCShared");

            using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
                Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default))
            {
                RegistryKey hkcu = Registry.CurrentUser;
                string basePath = @"SOFTWARE\WOW6432Node\Tencent\MobileGamePC";
                using (RegistryKey k = hklm.CreateSubKey(basePath))
                {
                    if (k == null) throw new InvalidOperationException("Cannot open " + basePath);

                    SetString(k, "version", "5371", false);
                    SetString(k, "TSyzsVersion", "4.1.143.90", false);
                    SetDword(k, "SupplyId", 900206429, false);
                }

                WritePath(hklm, basePath + @"\AEngine", "CpuScore", 10028, "GpuScore", 711, false);
                WriteInstallPath(hklm, basePath + @"\AOW_Rootfs_100", Path.Combine(install, "AOW_Rootfs_100"));
                SetStringAt(hklm, basePath + @"\AOW_Rootfs_100", "Version", "3.61.639.101", false);
                SetDwordAt(hklm, basePath + @"\AOW_Rootfs_100", "InstallDone", 1, false);

                WriteInstallPath(hklm, basePath + @"\AppMarket", Path.Combine(install, "AppMarket"));
                SetDwordAt(hklm, basePath + @"\AppMarket", "AutoStart", 0, false);
                SetStringAt(hklm, basePath + @"\AppMarket", "Version", "3.11.3149.80", false);
                SetQwordAt(hklm, basePath + @"\AppMarket", "InstallTime", DateTime.Now.ToFileTime(), false);

                WriteInstallPath(hklm, basePath + @"\UI", Path.Combine(install, "UI"));
                SetStringAt(hklm, basePath + @"\UI", "Version", "3.21.5371.80", false);
                SetDwordAt(hklm, basePath + @"\UI", "StartCount", 1, false);
                SetDwordAt(hklm, basePath + @"\UI", "LastTimeSuccess", 1, false);
                SetDwordAt(hklm, basePath + @"\UI", "local_opengl_start", 0, false);
                SetStringAt(hklm, basePath + @"\UI", "SnapshotPath", snapshotLink, true);

                SetStringAt(hklm, basePath + @"\UIData", "Version", "3.21.5371.80", false);
                SetQwordAt(hklm, basePath + @"\Update", "Time", DateTime.Now.ToFileTime(), false);

                string vdi = Path.Combine(install, "VDI_100_x64");
                if (Directory.Exists(vdi))
                {
                    WriteInstallPath(hklm, basePath + @"\VDI_100_x64", vdi);
                    SetDwordAt(hklm, basePath + @"\VDI_100_x64", "InstallDone", 1, false);
                }

                string hkcuPath = @"SOFTWARE\Tencent\MobileGamePC";
                using (RegistryKey k = hkcu.CreateSubKey(hkcuPath))
                {
                    if (k == null) throw new InvalidOperationException("Cannot open HKCU " + hkcuPath);
                    k.SetValue("TempPath", tempTarget, RegistryValueKind.String);
                    k.SetValue("sf", shared, RegistryValueKind.String);
                    SetString(k, "UserLanguage", "en", false);
                    SetString(k, "Region", "EG", false);
                    SetString(k, "syzs_country", "\u0645\u0635\u0631", false);
                    SetDword(k, "isInstall", 1, false);
                    SetDword(k, "UI_VerifyPeFile", 1, false);
                    SetDword(k, "FaceCheckSwitch", 1, false);
                    SetString(k, "AdaptedApkList", ";com.tencent.ig", false);
                    SetDword(k, "CPUScore", 10028, false);
                    SetDword(k, "tss_use_local_verify", 1, false);
                    SetDword(k, "dpiAware", 1, false);
                }

                using (RegistryKey k = hkcu.CreateSubKey(hkcuPath + @"\Beacon"))
                {
                    if (k != null)
                    {
                        SetString(k, "Last_Sid_AppMarket.exe", Guid.NewGuid().ToString().ToUpperInvariant(), false);
                        SetString(k, "Last_Sid_AndroidEmulatorEn.exe", Guid.NewGuid().ToString().ToUpperInvariant(), false);
                    }
                }
                using (RegistryKey k = hkcu.CreateSubKey(hkcuPath + @"\DeviceInfo"))
                {
                    if (k != null)
                    {
                        SetString(k, "glVendor", "Intel", false);
                        SetString(k, "glRender", "Intel(R) HD Graphics", false);
                    }
                }

                string uninstall = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\MobileGamePC";
                using (RegistryKey k = hklm.CreateSubKey(uninstall))
                {
                    if (k != null)
                    {
                        string appMarket = Path.Combine(install, "AppMarket", "AppMarket.exe");
                        if (!File.Exists(appMarket)) appMarket = FindEmulator();
                        k.SetValue("DisplayIcon", appMarket ?? Path.Combine(install, "GameLoop.exe"), RegistryValueKind.String);
                        k.SetValue("DisplayName", "GameLoop", RegistryValueKind.String);
                        k.SetValue("Publisher", "Tencent Technology Company", RegistryValueKind.String);
                        if (k.GetValue("DisplayVersion") == null)
                            k.SetValue("DisplayVersion", "4.1.143.90", RegistryValueKind.String);
                        k.SetValue("UninstallString",
                            Quote(Path.Combine(install, "Remove Gameloop.exe")), RegistryValueKind.String);
                    }
                }
            }
            Log("Registry paths patched to " + install);
        }

        private static void WritePath(RegistryKey root, string path, string name, int cpu, string gpuName, int gpu, bool overwrite)
        {
            using (RegistryKey k = root.CreateSubKey(path))
            {
                if (k == null) return;
                SetDword(k, name, cpu, overwrite);
                SetDword(k, gpuName, gpu, overwrite);
            }
        }

        private static void WriteInstallPath(RegistryKey root, string path, string value)
        {
            using (RegistryKey k = root.CreateSubKey(path))
            {
                if (k != null) k.SetValue("InstallPath", value, RegistryValueKind.String);
            }
        }

        private static void SetStringAt(RegistryKey root, string path, string name, string value, bool overwrite)
        {
            using (RegistryKey k = root.CreateSubKey(path))
                if (k != null) SetString(k, name, value, overwrite);
        }

        private static void SetDwordAt(RegistryKey root, string path, string name, int value, bool overwrite)
        {
            using (RegistryKey k = root.CreateSubKey(path))
                if (k != null) SetDword(k, name, value, overwrite);
        }

        private static void SetQwordAt(RegistryKey root, string path, string name, long value, bool overwrite)
        {
            using (RegistryKey k = root.CreateSubKey(path))
            {
                if (k == null) return;
                object current = k.GetValue(name, null);
                if (!overwrite && current != null) return;
                k.SetValue(name, value, RegistryValueKind.QWord);
            }
        }

        private static void SetString(RegistryKey key, string name, string value, bool overwrite)
        {
            if (!overwrite)
            {
                object current = key.GetValue(name, null);
                if (current != null && Convert.ToString(current).Length > 0) return;
            }
            key.SetValue(name, value ?? "", RegistryValueKind.String);
        }

        private static void SetDword(RegistryKey key, string name, int value, bool overwrite)
        {
            if (!overwrite && key.GetValue(name, null) != null) return;
            key.SetValue(name, value, RegistryValueKind.DWord);
        }

        private static string BackupFileName(string key)
        {
            string name = key.Replace(@"HKLM\", "hklm_").Replace(@"HKCU\", "hkcu_")
                             .Replace('\\', '_').Replace(':', '_');
            return name + ".reg";
        }

        // ------------------------------------------------------------------
        //  remove
        // ------------------------------------------------------------------

        private static int RemoveMode()
        {
            if (!AcquireMutex())
            {
                Log("Another apply/remove is already running.");
                return 0;
            }

            bool confirmed = _silent;
            if (!_silent)
            {
                DialogResult answer = MessageBox.Show(
                    "Remove GameLoop Portable links and registry entries?\r\n\r\n" +
                    "Root: " + _root + "\r\n\r\n" +
                    "The game files and Snapshot data stay in place.",
                    "GameLoop Portable - Remove", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes) return 0;
                confirmed = true;
            }
            if (!confirmed) return 0;

            Log((_dryRun ? "[dry-run] " : "") + "Stopping GameLoop processes and services...");
            if (!_dryRun) StopGameLoop();

            string snapshotLink = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Tencent", "TxGameAssistant", "Snapshot");
            string tempLink = TempLinkPath();

            RemoveOwnedLink(snapshotLink);
            RemoveOwnedLink(tempLink);

            if (!_dryRun) RestoreRegistry();
            else Log("[dry-run] would restore the registry backup.");

            Log("Remove finished.");
            FlushLog();
            if (!_silent)
            {
                MessageBox.Show("GameLoop Portable was removed.\r\n\r\n" +
                    "Links removed and the registry restored. Game files were kept.",
                    "GameLoop Portable - Remove", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return 0;
        }

        private static void RemoveOwnedLink(string path)
        {
            if (!IsReparsePoint(path)) { Log("Not a link, left alone: " + path); return; }
            string target = GetLinkTarget(path);
            if (string.IsNullOrEmpty(target) || !IsUnderRoot(target))
            {
                Log("Link is not ours, left alone: " + path + " -> " + (target ?? "unreadable"));
                return;
            }
            if (_dryRun) { Log("[dry-run] remove link " + path); return; }
            try
            {
                Directory.Delete(path, false);
                Log("Removed link: " + path);
            }
            catch (Exception ex) { Log("Could not remove link " + path + ": " + ex.Message); }
        }

        private static bool IsUnderRoot(string path)
        {
            try
            {
                string root = Path.GetFullPath(_root).TrimEnd('\\');
                string p = Path.GetFullPath(path).TrimEnd('\\');
                return string.Equals(p, root, StringComparison.OrdinalIgnoreCase) ||
                       p.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void RestoreRegistry()
        {
            try
            {
                string dir = Path.Combine(_root, "Registry", "backup");
                string state = Path.Combine(dir, "state.txt");
                string reg = Path.Combine(Environment.SystemDirectory, "reg.exe");

                if (File.Exists(state))
                {
                    bool ok = true;
                    int handled = 0;

                    foreach (string raw in File.ReadAllLines(state))
                    {
                        string line = (raw ?? "").Trim();
                        if (line.Length == 0) continue;
                        int bar = line.LastIndexOf('|');
                        if (bar <= 0) continue;

                        handled++;
                        string key = line.Substring(0, bar);
                        string status = line.Substring(bar + 1).Trim().ToLowerInvariant();

                        if (status == "present")
                        {
                            string file = Path.Combine(dir, BackupFileName(key));
                            if (File.Exists(file))
                            {
                                int code = RunTool(reg, "import " + Quote(file), false);
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
                            int code = RunTool(reg, "delete " + Quote(key) + " /f", false);
                            Log("Registry delete (exit " + code + "): " + key);
                        }
                    }

                    if (handled == 0)
                    {
                        Log("Registry backup state is empty; keeping it for inspection.");
                        return;
                    }
                    if (!ok)
                    {
                        Log("Registry restore was incomplete; the backup was kept for a retry.");
                        return;
                    }

                    try { Directory.Delete(dir, true); } catch { }
                }
                else if (RegistryPointsToRoot())
                {
                    // No backup: remove only the values this package wrote and
                    // keep unrelated Tencent settings (phone model, etc.).
                    RemoveOwnedValues();
                }
                else
                {
                    Log("Registry is not owned by this package; left alone.");
                }
            }
            catch (Exception ex) { Log("Registry restore failed: " + ex.Message); }
        }

        private static void RemoveOwnedValues()
        {
            try
            {
                string basePath = @"SOFTWARE\WOW6432Node\Tencent\MobileGamePC";
                using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
                    Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default))
                {
                    DeleteValueIfUnderRoot(hklm, basePath + @"\AOW_Rootfs_100", "InstallPath");
                    DeleteValueIfUnderRoot(hklm, basePath + @"\AppMarket", "InstallPath");
                    DeleteValueIfUnderRoot(hklm, basePath + @"\UI", "InstallPath");
                    DeleteValueIfUnderRoot(hklm, basePath + @"\VDI_100_x64", "InstallPath");

                    using (RegistryKey k = hklm.OpenSubKey(basePath + @"\UI", true))
                    {
                        if (k != null)
                        {
                            string snapshot = Convert.ToString(k.GetValue("SnapshotPath", ""));
                            if (snapshot.IndexOf(@"\Tencent\TxGameAssistant\Snapshot",
                                    StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                k.DeleteValue("SnapshotPath", false);
                                Log("Registry value removed: SnapshotPath");
                            }
                        }
                    }

                    string uninstall = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\MobileGamePC";
                    bool removeUninstall = false;
                    using (RegistryKey k = hklm.OpenSubKey(uninstall, true))
                    {
                        if (k != null)
                        {
                            string cmd = Convert.ToString(k.GetValue("UninstallString", ""));
                            removeUninstall = cmd.IndexOf(_root, StringComparison.OrdinalIgnoreCase) >= 0;
                        }
                    }
                    if (removeUninstall)
                    {
                        hklm.DeleteSubKeyTree(uninstall, false);
                        Log("Registry key removed: " + uninstall);
                    }
                }

                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Tencent\MobileGamePC", true))
                {
                    if (k != null)
                    {
                        string temp = Convert.ToString(k.GetValue("TempPath", ""));
                        string sf = Convert.ToString(k.GetValue("sf", ""));
                        if (temp.IndexOf(@"\Temp\TxGameDownload", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            sf.IndexOf(@"\Temp\TxGameDownload", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            k.DeleteValue("TempPath", false);
                            k.DeleteValue("sf", false);
                            Log("Registry values removed: TempPath / sf");
                        }
                    }
                }
            }
            catch (Exception ex) { Log("Registry value cleanup failed: " + ex.Message); }
        }

        private static void DeleteValueIfUnderRoot(RegistryKey root, string subKey, string name)
        {
            try
            {
                using (RegistryKey k = root.OpenSubKey(subKey, true))
                {
                    if (k == null) return;
                    string value = Convert.ToString(k.GetValue(name, ""));
                    if (string.IsNullOrEmpty(value) || !IsUnderRoot(value)) return;
                    k.DeleteValue(name, false);
                    Log("Registry value removed: " + subKey + "\\" + name);
                }
            }
            catch (Exception ex)
            {
                Log("Could not remove " + subKey + "\\" + name + ": " + ex.Message);
            }
        }

        private static bool RegistryPointsToRoot()
        {
            try
            {
                string basePath = @"SOFTWARE\WOW6432Node\Tencent\MobileGamePC";
                string[] subs = { "AOW_Rootfs_100", "AppMarket", "UI", "VDI_100_x64" };

                using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
                    Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default))
                {
                    foreach (string sub in subs)
                    {
                        using (RegistryKey k = hklm.OpenSubKey(basePath + "\\" + sub))
                        {
                            if (k == null) continue;
                            string install = Convert.ToString(k.GetValue("InstallPath", ""));
                            if (IsUnderRoot(install)) return true;
                        }
                    }

                    using (RegistryKey k = hklm.OpenSubKey(
                        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\MobileGamePC"))
                    {
                        if (k != null)
                        {
                            string cmd = Convert.ToString(k.GetValue("UninstallString", ""));
                            if (cmd.IndexOf(_root, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        // ------------------------------------------------------------------
        //  links / helpers
        // ------------------------------------------------------------------

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

                    if (!string.IsNullOrEmpty(actual) && Directory.Exists(actual) && !SamePath(actual, target))
                    {
                        Log("Migrating data from the previous target: " + actual);
                        if (!CopyMissing(actual, target, 0))
                        {
                            error = "Could not copy the previous data to the new target.\r\n" +
                                    "The old link was left in place. Close GameLoop and try again.";
                            return false;
                        }
                    }

                    if (!TryDeleteLink(real))
                    {
                        error = "Could not replace the existing link:\r\n" + real;
                        return false;
                    }
                }
                else if (Directory.Exists(real))
                {
                    Log("Adopting local data into the package: " + real);
                    if (!MergeMove(real, target, 0))
                    {
                        error = "Could not move this folder into the package (a file is in use):\r\n" + real;
                        return false;
                    }
                }
                else if (File.Exists(real))
                {
                    error = "A file occupies the link path:\r\n" + real;
                    return false;
                }

                string parent = Path.GetDirectoryName(real);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                if (!CreateSymbolicLink(real, target, 1))
                {
                    int code = Marshal.GetLastWin32Error();
                    error = "Creating the symbolic link failed (Win32 " + code + "):\r\n" + real +
                            "\r\n-> " + target +
                            (code == 1314 ? "\r\n\r\nAdministrator rights are required." : "");
                    Log("mklink failed (" + code + "): " + real + " -> " + target);
                    return false;
                }

                Log("Link created: " + real + " -> " + target);
                return true;
            }
            catch (Exception ex)
            {
                error = "Linking failed for " + real + ":\r\n" + ex.Message;
                return false;
            }
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

        private static bool IsNetworkPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                string full = Path.GetFullPath(path);
                if (full.StartsWith(@"\\", StringComparison.Ordinal)) return true;
                string root = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root)) return false;
                return new DriveInfo(root).DriveType == DriveType.Network;
            }
            catch { return false; }
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

        private static bool RelaunchElevated(string mode)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                psi.Arguments = "--elevated --mode " + mode +
                                (_noWait ? " --no-wait" : "") +
                                (_dryRun ? " --dry-run" : "") +
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

        private static bool AcquireMutex()
        {
            try
            {
                bool created;
                _mutex = new Mutex(true, @"Local\GameLoopPortable_" + _device, out created);
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
                        Log("Tool failed (" + p.ExitCode + "): " + Path.GetFileName(file) + " " +
                            arguments + " " + (stdout + stderr).Trim());
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                if (!quiet) Log("Tool failed: " + Path.GetFileName(file) + " : " + ex.Message);
                return -1;
            }
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
            try { candidates.Add(Path.Combine(_root, "Logs", _device + ".log")); } catch { }
            try
            {
                candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GameLoopPortable", "Logs", _device + ".log"));
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

        private static void Fail(string message)
        {
            Log("ERROR: " + message);
            FlushLog();
            if (_silent) return;
            MessageBox.Show(message, "GameLoop Portable", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
}


