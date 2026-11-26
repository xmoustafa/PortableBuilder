using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PortableLauncher
{
    /// <summary>
    /// Config-driven portable launcher.
    /// Redirects real user/system folders to a portable data folder using
    /// directory junctions (no admin) or symbolic links (admin), then imports
    /// registry files, ensures services and starts the target program silently.
    /// </summary>
    internal static class Program
    {
        private static string _baseDir;
        private static string _logPath;
        private static bool _silent;
        private static readonly StringBuilder _logBuffer = new StringBuilder();
        private static readonly List<UndoAction> _undo = new List<UndoAction>();
        private static readonly List<OverlayAction> _overlay = new List<OverlayAction>();
        private static readonly List<CopyFolderAction> _copyFolders = new List<CopyFolderAction>();
        private static string _backupRoot;
        private static int _overlayCounter;

        private sealed class UndoAction
        {
            public string Link;
            public string Backup;
        }

        private sealed class OverlayAction
        {
            public string Dest;
            public string Backup;
            public bool Existed;
            public string Package;
            public string Root;
        }

        private sealed class CopyFolderAction
        {
            public string Real;
            public string Backup;
            public string Package;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SERVICE_DESCRIPTION
        {
            public string lpDescription;
        }

        private struct SERVICE_STATUS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateSymbolicLink(string lpSymlinkFileName, string lpTargetFileName, uint dwFlags);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManager(string lpMachineName, string lpDatabaseName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateService(IntPtr hSCManager, string lpServiceName, string lpDisplayName,
            uint dwDesiredAccess, uint dwServiceType, uint dwStartType, uint dwErrorControl, string lpBinaryPathName,
            string lpLoadOrderGroup, IntPtr lpdwTagId, string lpDependencies, string lpServiceStartName, string lpPassword);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool StartService(IntPtr hService, int dwNumServiceArgs, string[] lpServiceArgVectors);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr hSCObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ChangeServiceConfig2(IntPtr hService, uint dwInfoLevel, ref SERVICE_DESCRIPTION lpInfo);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ControlService(IntPtr hService, uint dwControl, ref SERVICE_STATUS lpServiceStatus);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatus(IntPtr hService, ref SERVICE_STATUS lpServiceStatus);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DeleteService(IntPtr hService);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string lpExistingFileName, string lpNewFileName, uint dwFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(IntPtr hFile, System.Text.StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint SC_MANAGER_CONNECT = 0x0001;
        private const uint SC_MANAGER_CREATE_SERVICE = 0x0002;
        private const uint SERVICE_ALL_ACCESS = 0xF01FF;
        private const uint SERVICE_WIN32_OWN_PROCESS = 0x0010;
        private const uint SERVICE_AUTO_START = 0x0002;
        private const uint SERVICE_ERROR_NORMAL = 0x0001;
        private const uint SERVICE_CONFIG_DESCRIPTION = 0x0001;
        private const uint SERVICE_CONTROL_STOP = 0x00000001;
        private const uint SERVICE_STOPPED = 0x00000001;
        private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                _baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
                string configPath = Path.Combine(_baseDir, "launcher.ini");
                bool? silentOverride = null;
                bool uninstallArg = false;
                string autoDeleteFolder = null;

                for (int i = 0; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "--config", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        configPath = Path.GetFullPath(args[i + 1]);
                        i++;
                    }
                    else if (string.Equals(args[i], "--silent", StringComparison.OrdinalIgnoreCase))
                    {
                        silentOverride = true;
                    }
                    else if (string.Equals(args[i], "--no-silent", StringComparison.OrdinalIgnoreCase))
                    {
                        silentOverride = false;
                    }
                    else if (string.Equals(args[i], "--uninstall", StringComparison.OrdinalIgnoreCase))
                    {
                        uninstallArg = true;
                    }
                    else if (string.Equals(args[i], "--autodelete", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        autoDeleteFolder = args[++i];
                    }
                }

                if (!string.IsNullOrEmpty(autoDeleteFolder))
                    return AutoDelete(autoDeleteFolder);

                string embeddedText = null;
                if (!File.Exists(configPath))
                {
                    embeddedText = EmbeddedConfig.GetText();
                    if (string.IsNullOrEmpty(embeddedText)) embeddedText = ReadAppendedConfig();
                    if (string.IsNullOrEmpty(embeddedText))
                    {
                        Fail("Config not found (embedded or appended).\r\n" + configPath);
                        return 2;
                    }
                }

                IniFile ini;
                if (File.Exists(configPath)) ini = IniFile.Load(configPath);
                else ini = IniFile.Parse(embeddedText);

                _silent = silentOverride.HasValue ? silentOverride.Value : ini.GetBool("App", "Silent", true);
                string appName = ini.Get("App", "Name", "Portable App");
                string logSpec = ini.Get("App", "LogFile", "");
                _logPath = string.IsNullOrEmpty(logSpec == null ? null : logSpec.Trim()) ? "" : ExpandPath(logSpec);
                if (_logPath.Length > 0 && !IsUnderDirectory(_logPath, _baseDir))
                {
                    _logPath = "";
                    Log("LogFile outside the package ignored.");
                }

                Log("=== " + appName + " ===");
                Log("Root: " + _baseDir);

                // Self-elevate only when the config asks for it.
                if (ini.GetBool("App", "RequireAdmin", false) && !IsElevated())
                {
                    Log("Elevation required, relaunching with UAC...");
                    FlushLog();
                    if (!RelaunchElevated(configPath, _silent))
                    {
                        Fail("Administrator rights are required for: " + appName);
                        return 5;
                    }
                    return 0;
                }

                bool uninstallMode = uninstallArg || ini.GetBool("App", "Uninstall", false);
                if (uninstallMode)
                    return Uninstall(ini, configPath);

                string linkType = ini.Get("App", "LinkType", "Junction");
                bool wait = ini.GetBool("App", "WaitForExit", false);
                bool restore = ini.GetBool("App", "RestoreOnExit", false);
                int delay = ini.GetInt("App", "DelayMs", 0);

                // 1) Folder redirection: real path -> portable folder (fail-closed:
                // never run the app against host data if a required link fails).
                foreach (KeyValuePair<string, string> kv in ini.Items("Folders"))
                {
                    try
                    {
                        CreateFolderLink(ExpandPath(kv.Key), ExpandPath(kv.Value), linkType);
                    }
                    catch (Exception ex)
                    {
                        Log("FOLDER FAILED: " + kv.Key + " -> " + kv.Value + " : " + ex.Message);
                        RestoreLinks();
                        Fail("Portable redirection could not be established:\r\n" + kv.Key + "\r\n\r\n" + ex.Message);
                        return 10;
                    }
                }

                // 1b) Whole-folder copy redirection (works on FAT32/exFAT too)
                foreach (KeyValuePair<string, string> kv in ini.Items("CopyFolders"))
                {
                    try
                    {
                        ApplyCopyFolder(ExpandPath(kv.Key), ExpandPath(kv.Value));
                    }
                    catch (Exception ex)
                    {
                        Log("COPYFOLDER FAILED: " + kv.Key + " -> " + kv.Value + " : " + ex.Message);
                    }
                }

                // 1c) File overlay: copy individual files into shared/system folders
                foreach (KeyValuePair<string, string> kv in ini.Items("Overlay"))
                {
                    try
                    {
                        ApplyOverlay(ExpandPath(kv.Key), ExpandPath(kv.Value));
                    }
                    catch (Exception ex)
                    {
                        Log("OVERLAY FAILED: " + kv.Key + " -> " + kv.Value + " : " + ex.Message);
                    }
                }

                // 2) Registry files
                foreach (KeyValuePair<string, string> kv in ini.Items("Registry"))
                {
                    ImportRegistry(ExpandPath(kv.Value));
                }

                // 3) Services
                foreach (KeyValuePair<string, string> kv in ini.Items("Services"))
                {
                    string[] parts = SplitPipe(kv.Value, 3);
                    string binPath = parts[0];
                    string displayName = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : kv.Key;
                    string description = parts.Length > 2 ? parts[2] : "";
                    EnsureService(kv.Key, binPath, displayName, description);
                }

                if (delay > 0)
                {
                    Log("Delay " + delay + " ms");
                    Thread.Sleep(delay);
                }

                // 4) Programs
                List<Process> started = new List<Process>();
                foreach (KeyValuePair<string, string> kv in ini.Items("Run"))
                {
                    try
                    {
                        Process p = StartProgram(ExpandPath(kv.Key), kv.Value);
                        if (p != null)
                        {
                            started.Add(p);
                            Log("started: " + kv.Key + " " + kv.Value);
                        }
                        else
                        {
                            Log("RUN FAILED (null process): " + kv.Key);
                            RestoreLinks();
                            Fail("Could not start: " + kv.Key);
                            return 11;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("RUN FAILED: " + kv.Key + " : " + ex.Message);
                        RestoreLinks();
                        Fail("Could not start: " + kv.Key + "\r\n\r\n" + ex.Message);
                        return 11;
                    }
                }

                if (wait && started.Count > 0)
                {
                    Log("Waiting for " + started.Count + " process(es)...");
                    foreach (Process p in started)
                    {
                        try { p.WaitForExit(); }
                        catch { }
                        finally { try { p.Dispose(); } catch { } }
                    }
                }

                if (wait)
                {
                    // Some apps start a bootstrap exe that exits immediately.
                    // WaitProcess keeps the launcher alive until the real app closes.
                    string waitProcess = ini.Get("App", "WaitProcess", "").Trim();
                    if (waitProcess.Length > 0)
                    {
                        WaitForProcess(waitProcess, ini.GetInt("App", "WaitProcessGraceMs", 15000));
                    }

                    // Settings written at runtime go back into the portable package.
                    ExportRegistry(ini);
                    PersistOverlays();
                    PersistCopyFolders();
                }

                if (restore)
                {
                    if (!wait)
                    {
                        Log("RestoreOnExit ignored: set WaitForExit=1 so the app is closed first.");
                    }
                    else
                    {
                        RestoreOverlays();
                        RestoreCopyFolders();
                        RestoreLinks();
                    }
                }

                return 0;
            }
            catch (Exception ex)
            {
                Fail(ex.ToString());
                return 1;
            }
            finally
            {
                FlushLog();
            }
        }

        // ---------------------------------------------------------------- folders

        private static void CreateFolderLink(string link, string target, string linkType)
        {
            link = Path.GetFullPath(link);
            target = Path.GetFullPath(target);

            if (!IsSafeTarget(link))
                throw new InvalidOperationException("Refusing to redirect a protected/system path: " + link);

            if (string.Equals(link.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                Log("skip (same path): " + link);
                return;
            }
            if (IsDriveRoot(link))
            {
                throw new InvalidOperationException("Refusing to replace a drive root: " + link);
            }

            Directory.CreateDirectory(target);

            string backupPath = null;

            if (PathExists(link))
            {
                FileAttributes attr = File.GetAttributes(link);
                if ((attr & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(link, false); // removes link only, never the target
                    Log("removed old link: " + link);
                }
                else
                {
                    // Never destroy user data: rename the real folder/file instead.
                    string backup = link + ".portable-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                    if (File.Exists(link) && !Directory.Exists(link))
                    {
                        File.Move(link, backup);
                        Log("backed up real file: " + link + " -> " + backup);
                    }
                    else
                    {
                        Directory.Move(link, backup);
                        Log("backed up real folder: " + link + " -> " + backup);
                    }
                    backupPath = backup;
                }
            }
            else
            {
                // Handles a dangling junction/symlink.
                try { Directory.Delete(link, false); } catch { }
            }

            string parent = Path.GetDirectoryName(link);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            bool ok;
            try
            {
                if (string.Equals(linkType, "Symlink", StringComparison.OrdinalIgnoreCase))
                {
                    ok = CreateSymbolicLink(link, target, 1u);
                    if (!ok)
                    {
                        int err = Marshal.GetLastWin32Error();
                        throw new IOException("CreateSymbolicLink failed (Win32 " + err + "). " +
                            "Symbolic links need admin rights or Developer Mode.");
                    }
                }
                else
                {
                    ok = CreateJunction(link, target);
                    if (!ok)
                    {
                        throw new IOException("mklink /J failed. Target must be on a local NTFS volume.");
                    }
                }
            }
            catch
            {
                // Put the original folder back if the link could not be created.
                try
                {
                    if (PathExists(link))
                    {
                        FileAttributes attr = File.GetAttributes(link);
                        if ((attr & FileAttributes.ReparsePoint) != 0) Directory.Delete(link, false);
                    }
                    if (backupPath != null && !Directory.Exists(link) && !File.Exists(link))
                    {
                        if (Directory.Exists(backupPath)) Directory.Move(backupPath, link);
                        else if (File.Exists(backupPath)) File.Move(backupPath, link);
                    }
                }
                catch { }
                throw;
            }

            _undo.Add(new UndoAction { Link = link, Backup = backupPath });
            Log("link: " + link + " -> " + target);
        }

        private static bool CreateJunction(string link, string target)
        {
            string cmd = Environment.GetEnvironmentVariable("COMSPEC");
            if (string.IsNullOrEmpty(cmd)) cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            string args = "/d /c mklink /J " + Quote(link) + " " + Quote(target);
            int code = RunTool(cmd, args, true);
            return code == 0 && Directory.Exists(link);
        }

        private static void RestoreLinks()
        {
            for (int i = _undo.Count - 1; i >= 0; i--)
            {
                UndoAction u = _undo[i];
                try
                {
                    bool isLink = false;

                    try
                    {
                        DirectoryInfo di = new DirectoryInfo(u.Link);
                        if (di.Exists)
                        {
                            if ((di.Attributes & FileAttributes.ReparsePoint) != 0) isLink = true;
                        }
                        else
                        {
                            // A dangling junction/symlink: remove the reparse point.
                            try { Directory.Delete(u.Link, false); } catch { }
                        }
                    }
                    catch { }

                    if (isLink) Directory.Delete(u.Link, false);
                    if (u.Backup != null)
                    {
                        if (Directory.Exists(u.Backup) && !Directory.Exists(u.Link)) Directory.Move(u.Backup, u.Link);
                        else if (File.Exists(u.Backup) && !File.Exists(u.Link)) File.Move(u.Backup, u.Link);
                    }

                    Log("restored: " + u.Link);
                }
                catch (Exception ex)
                {
                    Log("restore failed: " + u.Link + " : " + ex.Message);
                }
            }
        }

        // ------------------------------------------------------------- copy folders

        private static void ApplyCopyFolder(string real, string package)
        {
            if (!IsSafeTarget(real))
            {
                Log("copyfolder refused (protected path): " + real);
                return;
            }
            if (!Directory.Exists(package))
            {
                Log("copyfolder missing: " + package);
                return;
            }
            if (File.Exists(real))
            {
                Log("copyfolder destination is a file, skipping: " + real);
                return;
            }

            string backup = null;
            if (Directory.Exists(real))
            {
                FileAttributes attr = File.GetAttributes(real);
                if ((attr & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(real, false);
                    Log("removed old link: " + real);
                }
                else
                {
                    backup = real.TrimEnd('\\') + ".portable-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                    Directory.Move(real, backup);
                    Log("copyfolder backed up: " + real + " -> " + backup);
                }
            }

            Directory.CreateDirectory(real);
            try
            {
                CopyDirectory(package, real);
            }
            catch
            {
                try
                {
                    if (Directory.Exists(real)) DeleteTreeSafe(real);
                    if (backup != null && Directory.Exists(backup)) Directory.Move(backup, real);
                }
                catch { }
                throw;
            }

            _copyFolders.Add(new CopyFolderAction { Real = real, Backup = backup, Package = package });
            Log("copyfolder: " + real + " <- " + package);
        }

        private static void PersistCopyFolders()
        {
            foreach (CopyFolderAction a in _copyFolders)
            {
                try
                {
                    if (Directory.Exists(a.Real)) MirrorDirectory(a.Real, a.Package);
                }
                catch (Exception ex)
                {
                    Log("copyfolder persist failed: " + a.Real + " : " + ex.Message);
                }
            }
        }

        private static void RestoreCopyFolders()
        {
            for (int i = _copyFolders.Count - 1; i >= 0; i--)
            {
                CopyFolderAction a = _copyFolders[i];
                try
                {
                    if (Directory.Exists(a.Real))
                    {
                        FileAttributes attr = File.GetAttributes(a.Real);
                        if ((attr & FileAttributes.ReparsePoint) != 0) Directory.Delete(a.Real, false);
                        else DeleteTreeSafe(a.Real);
                    }
                    if (a.Backup != null && Directory.Exists(a.Backup)) Directory.Move(a.Backup, a.Real);
                    Log("copyfolder restored: " + a.Real);
                }
                catch (Exception ex)
                {
                    Log("copyfolder restore failed: " + a.Real + " : " + ex.Message);
                }
            }
        }

        private static void CopyDirectory(string source, string dest)
        {
            CopyDirectoryRecursive(source, dest, 0);
        }

        private static void CopyDirectoryRecursive(string source, string dest, int depth)
        {
            if (depth > 32) return;
            Directory.CreateDirectory(dest);
            foreach (string file in Directory.EnumerateFiles(source))
            {
                string target = Path.Combine(dest, Path.GetFileName(file));
                ClearReadOnly(target);
                File.Copy(file, target, true);
            }
            foreach (string dir in Directory.EnumerateDirectories(source))
            {
                FileAttributes attr;
                try { attr = File.GetAttributes(dir); }
                catch { continue; }
                if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                CopyDirectoryRecursive(dir, Path.Combine(dest, Path.GetFileName(dir)), depth + 1);
            }
        }

        private static void MirrorDirectory(string source, string dest)
        {
            Directory.CreateDirectory(dest);

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            MirrorCopyRecursive(source, dest, "", seen, 0);

            List<string> destFiles = new List<string>(Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories));
            foreach (string file in destFiles)
            {
                string rel = file.Substring(dest.Length).TrimStart('\\');
                if (!seen.Contains(rel))
                {
                    try { File.Delete(file); }
                    catch (Exception ex) { Log("copyfolder delete failed: " + file + " : " + ex.Message); }
                }
            }
        }

        private static void MirrorCopyRecursive(string source, string dest, string prefix,
            HashSet<string> seen, int depth)
        {
            if (depth > 32) return;
            Directory.CreateDirectory(dest);

            foreach (string file in Directory.EnumerateFiles(source))
            {
                string name = Path.GetFileName(file);
                string rel = string.IsNullOrEmpty(prefix) ? name : prefix + "\\" + name;
                string target = Path.Combine(dest, name);
                ClearReadOnly(target);
                File.Copy(file, target, true);
                seen.Add(rel);
            }

            foreach (string dir in Directory.EnumerateDirectories(source))
            {
                FileAttributes attr;
                try { attr = File.GetAttributes(dir); }
                catch { continue; }
                if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                string name = Path.GetFileName(dir);
                string rel = string.IsNullOrEmpty(prefix) ? name : prefix + "\\" + name;
                MirrorCopyRecursive(dir, Path.Combine(dest, name), rel, seen, depth + 1);
            }
        }

        private static void ClearReadOnly(string file)
        {
            try
            {
                if (!File.Exists(file)) return;
                FileAttributes attr = File.GetAttributes(file);
                if ((attr & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attr & ~FileAttributes.ReadOnly);
            }
            catch { }
        }

        // Deletes a tree without ever following a junction/symlink. A plain
        // Directory.Delete(path, true) can walk into a reparse point and erase
        // data outside the package.
        private static void DeleteTreeSafe(string path)
        {
            try
            {
                if (!Directory.Exists(path)) return;
                FileAttributes attr = File.GetAttributes(path);
                if ((attr & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(path, false);
                    return;
                }
                foreach (string file in Directory.GetFiles(path))
                {
                    try { ClearReadOnly(file); File.Delete(file); } catch { }
                }
                foreach (string dir in Directory.GetDirectories(path))
                {
                    try
                    {
                        FileAttributes da = File.GetAttributes(dir);
                        if ((da & FileAttributes.ReparsePoint) != 0) Directory.Delete(dir, false);
                        else DeleteTreeSafe(dir);
                    }
                    catch { }
                }
                if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 0)
                    Directory.Delete(path, false);
            }
            catch { }
        }

        // ----------------------------------------------------------------- overlay

        private static void ApplyOverlay(string realRoot, string packageDir)
        {
            if (!IsSafeTarget(realRoot))
            {
                Log("overlay refused (protected path): " + realRoot);
                return;
            }
            if (!Directory.Exists(packageDir))
            {
                Log("overlay missing: " + packageDir);
                return;
            }
            if (!Directory.Exists(realRoot)) Directory.CreateDirectory(realRoot);

            foreach (string src in Directory.EnumerateFiles(packageDir, "*", SearchOption.AllDirectories))
            {
                string rel = src.Substring(packageDir.Length).TrimStart('\\');
                string dest = Path.Combine(realRoot, rel);
                if (Directory.Exists(dest))
                {
                    Log("overlay skip (destination is a folder): " + dest);
                    continue;
                }

                bool existed = File.Exists(dest);
                string backup = null;

                if (existed)
                {
                    if (_backupRoot == null)
                    {
                        _backupRoot = Path.Combine(_baseDir, "Backup", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                    }
                    Directory.CreateDirectory(_backupRoot);
                    backup = Path.Combine(_backupRoot, (++_overlayCounter).ToString("D5") + ".bak");
                    try
                    {
                        File.Copy(dest, backup, true);
                    }
                    catch (Exception ex)
                    {
                        Log("overlay backup failed, skipping " + dest + " : " + ex.Message);
                        continue;
                    }
                }

                string parent = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                try
                {
                    FileAttributes attrs = File.GetAttributes(dest);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(dest, attrs & ~FileAttributes.ReadOnly);
                }
                catch { }

                try
                {
                    File.Copy(src, dest, true);
                }
                catch (Exception ex)
                {
                    try
                    {
                        if (backup != null && File.Exists(backup)) File.Copy(backup, dest, true);
                        else if (!existed && File.Exists(dest)) File.Delete(dest);
                    }
                    catch { }
                    Log("overlay copy failed: " + dest + " : " + ex.Message);
                    continue;
                }

                _overlay.Add(new OverlayAction
                {
                    Dest = dest,
                    Backup = backup,
                    Existed = existed,
                    Package = src,
                    Root = realRoot
                });
                Log("overlay: " + dest);
            }
        }

        private static void PersistOverlays()
        {
            foreach (OverlayAction a in _overlay)
            {
                try
                {
                    if (File.Exists(a.Dest))
                    {
                        string parent = Path.GetDirectoryName(a.Package);
                        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                        File.Copy(a.Dest, a.Package, true);
                    }
                }
                catch (Exception ex)
                {
                    Log("overlay persist failed: " + a.Dest + " : " + ex.Message);
                }
            }
        }

        private static void RestoreOverlays()
        {
            for (int i = _overlay.Count - 1; i >= 0; i--)
            {
                OverlayAction a = _overlay[i];
                try
                {
                    if (a.Existed)
                    {
                        if (a.Backup != null && File.Exists(a.Backup))
                        {
                            ClearReadOnly(a.Dest);
                            File.Copy(a.Backup, a.Dest, true);
                        }
                    }
                    else if (File.Exists(a.Dest))
                    {
                        ClearReadOnly(a.Dest);
                        File.Delete(a.Dest);
                    }
                    Log("overlay restored: " + a.Dest);
                    RemoveEmptyParents(a.Dest, a.Root);
                }
                catch (Exception ex)
                {
                    Log("overlay restore failed: " + a.Dest + " : " + ex.Message);
                }
            }
        }

        private static void RemoveEmptyParents(string file, string root)
        {
            try
            {
                string rootFull = Path.GetFullPath(root).TrimEnd('\\');
                string dir = Path.GetDirectoryName(file);

                while (!string.IsNullOrEmpty(dir))
                {
                    string full = Path.GetFullPath(dir).TrimEnd('\\');
                    if (full.Length <= rootFull.Length) break;
                    if (!full.StartsWith(rootFull + "\\", StringComparison.OrdinalIgnoreCase)) break;

                    if (Directory.Exists(full) && Directory.GetFileSystemEntries(full).Length == 0)
                    {
                        Directory.Delete(full);
                        dir = Path.GetDirectoryName(full);
                    }
                    else
                    {
                        break;
                    }
                }
            }
            catch { }
        }

        // ---------------------------------------------------------------- registry

        private static void ExportRegistry(IniFile ini)
        {
            foreach (KeyValuePair<string, string> kv in ini.Items("RegistryExport"))
            {
                string key = NormalizeHive(kv.Key);
                string file = ExpandPath(kv.Value);
                if (!IsUnderDirectory(file, _baseDir))
                {
                    Log("registry export target outside the package ignored: " + file);
                    continue;
                }
                try
                {
                    string parent = Path.GetDirectoryName(file);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                    string reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
                    try { if (File.Exists(file)) File.Delete(file); } catch { }
                    int code = RunTool(reg, "export " + Quote(key) + " " + Quote(file) + " /y", true);
                    if (code != 0 && Environment.Is64BitOperatingSystem)
                        code = RunTool(reg, "export " + Quote(key) + " " + Quote(file) + " /y /reg:32", true);
                    Log("reg export (exit " + code + "): " + key + " -> " + file);
                }
                catch (Exception ex)
                {
                    Log("reg export failed: " + key + " : " + ex.Message);
                }
            }
        }

        private static string NormalizeHive(string key)
        {
            string k = key.Trim();
            if (k.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase)) return "HKEY_CURRENT_USER\\" + k.Substring(5);
            if (k.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase)) return "HKEY_LOCAL_MACHINE\\" + k.Substring(5);
            if (k.StartsWith("HKCR\\", StringComparison.OrdinalIgnoreCase)) return "HKEY_CLASSES_ROOT\\" + k.Substring(5);
            if (k.StartsWith("HKU\\", StringComparison.OrdinalIgnoreCase)) return "HKEY_USERS\\" + k.Substring(4);
            if (string.Equals(k, "HKCU", StringComparison.OrdinalIgnoreCase)) return "HKEY_CURRENT_USER";
            if (string.Equals(k, "HKLM", StringComparison.OrdinalIgnoreCase)) return "HKEY_LOCAL_MACHINE";
            if (string.Equals(k, "HKCR", StringComparison.OrdinalIgnoreCase)) return "HKEY_CLASSES_ROOT";
            if (string.Equals(k, "HKU", StringComparison.OrdinalIgnoreCase)) return "HKEY_USERS";
            return k;
        }

        // --------------------------------------------------------------- registry

        private static void ImportRegistry(string file)
        {
            if (!IsUnderDirectory(file, _baseDir))
            {
                Log("registry file outside the package ignored: " + file);
                return;
            }
            if (!File.Exists(file))
            {
                Log("registry file missing: " + file);
                return;
            }
            BackupRegistry(file);
            string reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
            int code = RunTool(reg, "import " + Quote(file), true);
            Log("reg import (exit " + code + "): " + file);
        }

        // Backs up the pre-existing state of every key in the .reg file so the
        // uninstaller can restore it instead of blindly deleting user data.
        private static void BackupRegistry(string file)
        {
            try
            {
                string backup = file + ".original";
                if (File.Exists(backup)) return;

                List<string> keys = new List<string>();
                foreach (string raw in File.ReadAllLines(file))
                {
                    string line = raw.Trim();
                    if (line.Length < 3 || line[0] != '[' || line[line.Length - 1] != ']') continue;
                    string key = line.Substring(1, line.Length - 2).Trim();
                    if (key.StartsWith("-")) continue;
                    keys.Add(NormalizeHive(key));
                }
                if (keys.Count == 0) return;

                string dir = Path.GetDirectoryName(file);
                if (string.IsNullOrEmpty(dir)) return;

                string reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
                List<string> temps = new List<string>();
                foreach (string key in keys)
                {
                    string temp = Path.Combine(dir, "._bk_" + Guid.NewGuid().ToString("N") + ".reg");
                    if (RunTool(reg, "export " + Quote(key) + " " + Quote(temp) + " /y", true) == 0)
                        temps.Add(temp);
                }
                if (temps.Count == 0) return;

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Windows Registry Editor Version 5.00");
                sb.AppendLine();
                foreach (string t in temps)
                {
                    try
                    {
                        foreach (string line in File.ReadAllLines(t))
                        {
                            if (line.TrimStart().StartsWith("Windows Registry Editor")) continue;
                            sb.AppendLine(line);
                        }
                    }
                    catch { }
                    try { File.Delete(t); } catch { }
                }
                File.WriteAllText(backup, sb.ToString(), Encoding.Unicode);
                Log("registry backed up: " + backup);
            }
            catch (Exception ex) { Log("registry backup failed: " + ex.Message); }
        }

        // --------------------------------------------------------------- services

        private static void EnsureService(string name, string binPath, string displayName, string description)
        {
            string exe = CommandExecutable(binPath);
            if (string.IsNullOrEmpty(exe) || !IsSafeTarget(exe))
            {
                Log("service refused (unsafe binary path): " + name + " -> " + binPath);
                return;
            }
            IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT | SC_MANAGER_CREATE_SERVICE);
            if (scm == IntPtr.Zero)
            {
                Log("OpenSCManager failed (Win32 " + Marshal.GetLastWin32Error() + ")");
                return;
            }

            IntPtr service = IntPtr.Zero;
            try
            {
                service = OpenService(scm, name, SERVICE_ALL_ACCESS);
                if (service == IntPtr.Zero)
                {
                    service = CreateService(scm, name, displayName, SERVICE_ALL_ACCESS,
                        SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START, SERVICE_ERROR_NORMAL,
                        binPath, null, IntPtr.Zero, null, null, null);

                    if (service == IntPtr.Zero)
                    {
                        Log("CreateService '" + name + "' failed (Win32 " + Marshal.GetLastWin32Error() + ")");
                        return;
                    }

                    Log("service created: " + name);

                    if (description.Length > 0)
                    {
                        SERVICE_DESCRIPTION info = new SERVICE_DESCRIPTION();
                        info.lpDescription = description;
                        ChangeServiceConfig2(service, SERVICE_CONFIG_DESCRIPTION, ref info);
                    }
                }
                else
                {
                    Log("service exists: " + name);
                }

                if (StartService(service, 0, null))
                {
                    Log("service started: " + name);
                }
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    // 1056 = ERROR_SERVICE_ALREADY_RUNNING
                    Log(err == 1056 ? "service already running: " + name
                                    : "StartService '" + name + "' failed (Win32 " + err + ")");
                }
            }
            finally
            {
                if (service != IntPtr.Zero) CloseServiceHandle(service);
                CloseServiceHandle(scm);
            }
        }

        // ---------------------------------------------------------------- process

        private static Process StartProgram(string file, string arguments)
        {
            if (!Path.IsPathRooted(file)) file = Path.Combine(_baseDir, file);

            string workDir = Path.GetDirectoryName(file);
            ProcessStartInfo psi = new ProcessStartInfo(file, arguments);
            psi.UseShellExecute = true;
            psi.WorkingDirectory = workDir;
            return Process.Start(psi);
        }

        private static int RunTool(string file, string args, bool capture)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(file, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = capture;
                psi.RedirectStandardError = capture;

                using (Process p = Process.Start(psi))
                {
                    System.Threading.Tasks.Task<string> outTask = capture ? p.StandardOutput.ReadToEndAsync() : null;
                    System.Threading.Tasks.Task<string> errTask = capture ? p.StandardError.ReadToEndAsync() : null;
                    bool exited = p.WaitForExit(120000);
                    if (!exited)
                    {
                        try { p.Kill(); } catch { }
                        try { p.WaitForExit(5000); } catch { }
                    }

                    if (capture)
                    {
                        string text = "";
                        try { if (outTask.Wait(5000)) text += outTask.Result; } catch { }
                        try { if (errTask.Wait(5000)) text += " " + errTask.Result; } catch { }
                        text = text.Trim();
                        if (text.Length > 0) Log("  > " + text.Replace("\r\n", " | "));
                    }
                    if (!exited)
                    {
                        Log("tool timed out: " + file);
                        return -1;
                    }
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                Log("tool failed: " + file + " : " + ex.Message);
                return -1;
            }
        }

        // ------------------------------------------------------------------ misc

        private static void WaitForProcess(string name, int graceMs)
        {
            bool seen = false;
            Stopwatch sw = Stopwatch.StartNew();

            while (true)
            {
                Process[] list = Process.GetProcessesByName(name);
                bool running = list.Length > 0;
                foreach (Process p in list) p.Dispose();

                if (running) seen = true;
                if (!running && seen) break;
                if (!running && !seen && sw.ElapsedMilliseconds >= graceMs) break;

                Thread.Sleep(500);
            }

            Log("wait process finished: " + name + (seen ? "" : " (never seen)"));
        }

        private static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool RelaunchElevated(string configPath, bool silent)
        {
            try
            {
                string exe = Assembly.GetExecutingAssembly().Location;
                string args = "--config " + Quote(configPath) + (silent ? " --silent" : " --no-silent");

                ProcessStartInfo psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                using (Process child = Process.Start(psi)) { }
                return true;
            }
            catch (Exception ex)
            {
                Log("elevation cancelled/failed: " + ex.Message);
                return false;
            }
        }

        private static string ExpandPath(string value)
        {
            string expanded = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
            if (!Path.IsPathRooted(expanded)) expanded = Path.Combine(_baseDir, expanded);
            return Path.GetFullPath(expanded);
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string[] SplitPipe(string value, int max)
        {
            return value.Split(new char[] { '|' }, max);
        }

        // -------------------------------------------------------------- uninstall

        private static int Uninstall(IniFile ini, string configPath)
        {
            string appName = ini.Get("App", "Name", "App");
            Log("=== Uninstall: " + appName + " ===");

            bool deletePackage = false;
            if (!_silent)
            {
                DialogResult answer = MessageBox.Show(
                    "Uninstall \"" + appName + "\"?\r\n\r\n" +
                    "This removes the symlinks/junctions, services, and registry entries " +
                    "created by this package.\r\n\r\n" +
                    "Close the app before continuing.\r\n\r\n" +
                    "Also delete the package folder?\r\n" +
                    "(Yes = delete folder, No = keep folder, Cancel = abort)",
                    "Uninstall " + appName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                if (answer == DialogResult.Cancel) { Log("Uninstall cancelled."); return 0; }
                deletePackage = (answer == DialogResult.Yes);
            }

            // 1) services
            foreach (KeyValuePair<string, string> kv in ini.Items("Services"))
            {
                string svcName = kv.Key;
                string expected = kv.Value;
                int bar = expected.IndexOf('|');
                if (bar >= 0) expected = expected.Substring(0, bar);
                StopAndDeleteService(svcName, expected);
            }

            // 2) folder links (restore backups)
            foreach (KeyValuePair<string, string> kv in ini.Items("Folders"))
            {
                try { RemoveLinkAndRestore(ExpandPath(kv.Key), ExpandPath(kv.Value)); }
                catch (Exception ex) { Log("folder uninstall failed: " + kv.Key + " : " + ex.Message); }
            }

            // 2b) copy folders (keep the working copy aside, restore the original)
            foreach (KeyValuePair<string, string> kv in ini.Items("CopyFolders"))
            {
                try { RestoreCopyFolderOnUninstall(ExpandPath(kv.Key)); }
                catch (Exception ex) { Log("copyfolder uninstall failed: " + kv.Key + " : " + ex.Message); }
            }

            // 3) registry keys listed in [Registry] files
            foreach (KeyValuePair<string, string> kv in ini.Items("Registry"))
            {
                try { DeleteRegistryFile(ExpandPath(kv.Value)); }
                catch (Exception ex) { Log("registry uninstall failed: " + kv.Value + " : " + ex.Message); }
            }

            Log("Uninstall finished.");

            if (deletePackage)
            {
                // Copy ourselves to temp, then remove the package from there so the
                // running EXE is not locked while we delete its folder.
                try
                {
                    string temp = Path.Combine(Path.GetTempPath(), "Uninstall_" + Guid.NewGuid().ToString("N") + ".exe");
                    File.Copy(Application.ExecutablePath, temp, true);
                    Process.Start(temp, "--autodelete " + Quote(_baseDir));
                    Log("package cleanup scheduled: " + _baseDir);
                    return 0;
                }
                catch (Exception ex)
                {
                    Log("package cleanup failed: " + ex.Message);
                }
            }

            if (!_silent)
                MessageBox.Show("Uninstall finished for \"" + appName + "\"." +
                    (deletePackage ? "" : "\r\n\r\nYou can now delete the package folder."),
                    "Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        private static int AutoDelete(string folder)
        {
            try
            {
                string full = Path.GetFullPath(folder).TrimEnd('\\');
                bool isRoot = full.Length <= 2 || (full.Length == 3 && full[1] == ':');
                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
                bool isProfile = string.Equals(full, profile, StringComparison.OrdinalIgnoreCase);
                bool looksLikePackage = Directory.Exists(full) &&
                    Directory.GetDirectories(full, "*Data", SearchOption.TopDirectoryOnly).Length > 0;

                if (isRoot || isProfile || !IsSafeTarget(full) || !looksLikePackage)
                {
                    Log("autodelete refused (unsafe folder): " + full);
                    return 0;
                }

                System.Threading.Thread.Sleep(2500);
                for (int attempt = 0; attempt < 6; attempt++)
                {
                    DeleteTreeSafe(full);
                    if (!Directory.Exists(full)) break;
                    System.Threading.Thread.Sleep(1000);
                }
                Log("autodelete: " + full + " exists=" + Directory.Exists(full));
            }
            catch (Exception ex) { Log("autodelete failed: " + ex.Message); }
            try { MoveFileEx(Application.ExecutablePath, null, MOVEFILE_DELAY_UNTIL_REBOOT); } catch { }
            return 0;
        }

        private static void RestoreCopyFolderOnUninstall(string real)
        {
            try
            {
                if (!IsSafeTarget(real))
                {
                    Log("copyfolder restore refused (protected path): " + real);
                    return;
                }
                string full = Path.GetFullPath(real);
                string parent = Path.GetDirectoryName(full);
                if (string.IsNullOrEmpty(parent)) return;

                string pattern = Path.GetFileName(full.TrimEnd('\\')) + ".portable-backup-*";
                string[] backups = Directory.GetDirectories(parent, pattern);
                if (backups.Length == 0) return;

                Array.Sort(backups, StringComparer.OrdinalIgnoreCase);
                string newest = backups[backups.Length - 1];

                if (Directory.Exists(full))
                {
                    FileAttributes attr = File.GetAttributes(full);
                    if ((attr & FileAttributes.ReparsePoint) != 0)
                    {
                        Directory.Delete(full, false);
                    }
                    else
                    {
                        string keep = full.TrimEnd('\\') + ".portable-data-" +
                                      DateTime.Now.ToString("yyyyMMdd-HHmmss");
                        Directory.Move(full, keep);
                        Log("copyfolder working copy kept: " + keep);
                    }
                }

                Directory.Move(newest, full);
                Log("copyfolder restored: " + full);
            }
            catch (Exception ex)
            {
                Log("copyfolder restore failed: " + real + " : " + ex.Message);
            }
        }

        private static void StopAndDeleteService(string name, string expectedBinPath)
        {
            IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) { Log("OpenSCManager failed (Win32 " + Marshal.GetLastWin32Error() + ")"); return; }
            IntPtr svc = IntPtr.Zero;
            try
            {
                svc = OpenService(scm, name, SERVICE_ALL_ACCESS);
                if (svc == IntPtr.Zero) { Log("service not present: " + name); return; }

                // Ownership: only delete services that point into this package.
                string current = GetServiceImagePath(name);
                if (!string.IsNullOrEmpty(current) && !string.IsNullOrEmpty(expectedBinPath))
                {
                    string dir = "";
                    try { dir = Path.GetDirectoryName(expectedBinPath); } catch { }
                    bool ours = IsUnderDirectory(current, _baseDir) ||
                                (!string.IsNullOrEmpty(dir) && IsUnderDirectory(current, dir));
                    if (!ours)
                    {
                        Log("skip service (pre-existing, not ours): " + name);
                        return;
                    }
                }

                SERVICE_STATUS status = new SERVICE_STATUS();
                if (ControlService(svc, SERVICE_CONTROL_STOP, ref status))
                    Log("service stop requested: " + name);

                for (int i = 0; i < 20; i++)
                {
                    SERVICE_STATUS st = new SERVICE_STATUS();
                    if (!QueryServiceStatus(svc, ref st) || st.dwCurrentState == SERVICE_STOPPED) break;
                    System.Threading.Thread.Sleep(250);
                }

                if (DeleteService(svc)) Log("service deleted: " + name);
                else Log("DeleteService failed (Win32 " + Marshal.GetLastWin32Error() + "): " + name);
            }
            finally
            {
                if (svc != IntPtr.Zero) CloseServiceHandle(svc);
                CloseServiceHandle(scm);
            }
        }

        private static bool IsUnderDirectory(string path, string dir)
        {
            try
            {
                string p = Path.GetFullPath(path).TrimEnd('\\');
                string d = Path.GetFullPath(dir).TrimEnd('\\');
                return string.Equals(p, d, StringComparison.OrdinalIgnoreCase) ||
                       p.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string GetServiceImagePath(string name)
        {
            try
            {
                string reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
                ProcessStartInfo psi = new ProcessStartInfo(reg,
                    "query \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\" + name + "\" /v ImagePath");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = false;
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return null;
                    System.Threading.Tasks.Task<string> read = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(10000))
                    {
                        try { p.Kill(); } catch { }
                        return null;
                    }
                    string output = read.Wait(5000) ? read.Result : "";
                    foreach (string line in output.Split('\n'))
                    {
                        string l = line.Trim();
                        if (!l.StartsWith("ImagePath", StringComparison.OrdinalIgnoreCase)) continue;
                        int idx = l.IndexOf("REG_", StringComparison.OrdinalIgnoreCase);
                        if (idx < 0) continue;
                        int sp = l.IndexOf(' ', idx);
                        if (sp < 0) continue;
                        string val = l.Substring(sp + 1).Trim();
                        if (val.StartsWith("\"") && val.Length > 1)
                        {
                            int q = val.IndexOf('"', 1);
                            if (q > 0) val = val.Substring(1, q - 1);
                        }
                        else
                        {
                            int sp2 = val.IndexOf(' ');
                            if (sp2 > 0) val = val.Substring(0, sp2);
                        }
                        return val;
                    }
                }
            }
            catch { }
            return null;
        }

        private static void RemoveLinkAndRestore(string real, string target)
        {
            try { real = Path.GetFullPath(real); }
            catch { Log("skip uninstall target (invalid path): " + real); return; }
            if (!IsSafeTarget(real))
            {
                Log("skip uninstall target (protected path): " + real);
                return;
            }
            bool removed = false;
            try
            {
                if (PathExists(real))
                {
                    FileAttributes attr = File.GetAttributes(real);
                    if ((attr & FileAttributes.ReparsePoint) != 0)
                    {
                        if (!IsPackageLink(real, target))
                        {
                            Log("skip (foreign reparse point, not ours): " + real);
                        }
                        else
                        {
                            Directory.Delete(real, false);
                            removed = true;
                            Log("removed link: " + real);
                        }
                    }
                    else
                    {
                        Log("skip (real folder, not a link): " + real);
                    }
                }
            }
            catch (Exception ex) { Log("remove link failed: " + real + " : " + ex.Message); }

            if (!removed) return;

            try
            {
                string parent = Path.GetDirectoryName(real);
                if (string.IsNullOrEmpty(parent)) return;
                string[] backups = Directory.GetDirectories(parent, Path.GetFileName(real) + ".portable-backup-*");
                if (backups.Length == 0)
                {
                    string[] fileBackups = Directory.GetFiles(parent, Path.GetFileName(real) + ".portable-backup-*");
                    if (fileBackups.Length > 0 && !PathExists(real))
                    {
                        Array.Sort(fileBackups, StringComparer.OrdinalIgnoreCase);
                        File.Move(fileBackups[fileBackups.Length - 1], real);
                        Log("restored file backup: " + real);
                    }
                    return;
                }
                Array.Sort(backups, StringComparer.OrdinalIgnoreCase);
                string newest = backups[backups.Length - 1];
                if (!PathExists(real))
                {
                    Directory.Move(newest, real);
                    Log("restored backup: " + newest + " -> " + real);
                }
            }
            catch (Exception ex) { Log("restore backup failed: " + ex.Message); }
        }

        private static void DeleteRegistryFile(string file)
        {
            if (!IsUnderDirectory(file, _baseDir))
            {
                Log("registry file outside the package ignored: " + file);
                return;
            }
            if (!File.Exists(file)) { Log("registry file missing: " + file); return; }
            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch (Exception ex) { Log("read registry file failed: " + file + " : " + ex.Message); return; }

            string backup = file + ".original";
            HashSet<string> originalKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(backup))
            {
                try
                {
                    foreach (string raw in File.ReadAllLines(backup))
                    {
                        string line = raw.Trim();
                        if (line.Length < 3 || line[0] != '[' || line[line.Length - 1] != ']') continue;
                        string key = line.Substring(1, line.Length - 2).Trim();
                        if (key.StartsWith("-")) key = key.Substring(1).Trim();
                        if (key.Length > 0) originalKeys.Add(NormalizeHive(key));
                    }
                }
                catch { }
            }

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length < 3 || line[0] != '[' || line[line.Length - 1] != ']') continue;
                string key = line.Substring(1, line.Length - 2).Trim();
                if (key.StartsWith("-")) continue;

                // Keys that already existed before the first import carry user
                // data; restoring the backup is enough and is safer than
                // deleting the whole key (which would drop subkeys added since).
                if (originalKeys.Contains(NormalizeHive(key)))
                {
                    Log("registry key pre-existed; restored instead of deleted: " + key);
                    continue;
                }
                DeleteRegistryKey(key);
            }

            // Restore the pre-existing keys captured before the first import.
            if (File.Exists(backup))
            {
                try
                {
                    string reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
                    int code = RunTool(reg, "import " + Quote(backup), true);
                    Log("reg restore original (exit " + code + "): " + backup);
                    if (code == 0)
                    {
                        // The captured pre-import state is now consumed. Deleting
                        // it makes the next run capture a fresh baseline instead
                        // of restoring a stale one.
                        try { File.Delete(backup); } catch { }
                    }
                }
                catch (Exception ex) { Log("reg restore failed: " + ex.Message); }
            }
        }

        private static void DeleteRegistryKey(string key)
        {
            string k = NormalizeHive(key);
            if (!k.StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase) &&
                !k.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase) &&
                !k.StartsWith("HKEY_CLASSES_ROOT", StringComparison.OrdinalIgnoreCase) &&
                !k.StartsWith("HKEY_USERS", StringComparison.OrdinalIgnoreCase))
            {
                Log("skip registry key (unknown hive): " + key);
                return;
            }
            try
            {
                string reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
                int code = RunTool(reg, "delete " + Quote(k) + " /f", true);
                Log("reg delete (exit " + code + "): " + k);
            }
            catch (Exception ex) { Log("reg delete failed: " + k + " : " + ex.Message); }
        }

        private const string AppendedMarker = ";;PB2CFG;;";
        private const string AppendedEnd = ";;PB2END;;";

        // Reads the config appended to this EXE's own file (temp-free packaging).
        private static string ReadAppendedConfig()
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(Application.ExecutablePath);
                byte[] marker = Encoding.ASCII.GetBytes(AppendedMarker);
                byte[] endm = Encoding.ASCII.GetBytes(AppendedEnd);
                int start = -1;
                for (int i = bytes.Length - marker.Length; i >= 0; i--) { if (BytesMatch(bytes, marker, i)) { start = i; break; } }
                if (start < 0) return null;
                start += marker.Length;
                int end = -1;
                for (int i = start; i <= bytes.Length - endm.Length; i++) { if (BytesMatch(bytes, endm, i)) { end = i; break; } }
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

        // Resolves a directory link to its final target (owner check for uninstall).
        private static string GetLinkTarget(string path)
        {
            IntPtr h = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (h == new IntPtr(-1)) return null;
            try
            {
                System.Text.StringBuilder sb = new System.Text.StringBuilder(1024);
                uint n = GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, 0);
                if (n == 0) return null;
                string result = sb.ToString();
                result = NormalizeFinalPath(result);
                return result;
            }
            finally { CloseHandle(h); }
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

        private static bool IsPackageLink(string real, string expectedTarget)
        {
            string actual = GetLinkTarget(real);
            if (string.IsNullOrEmpty(actual)) return false;
            try
            {
                return string.Equals(
                    Path.GetFullPath(actual).TrimEnd('\\'),
                    Path.GetFullPath(expectedTarget).TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool PathExists(string path)
        {
            try
            {
                File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch { return false; }
        }

        private static bool IsDriveRoot(string path)
        {
            string full = Path.GetFullPath(path).TrimEnd('\\');
            return full.Length == 2 && full[1] == ':';
        }

        private static string CommandExecutable(string command)
        {
            if (string.IsNullOrEmpty(command)) return "";
            string s = command.Trim();
            if (s.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = s.IndexOf('"', 1);
                return end > 1 ? s.Substring(1, end - 1).Trim() : "";
            }
            int space = s.IndexOf(' ');
            return (space > 0 ? s.Substring(0, space) : s).Trim();
        }

        // Refuses destructive redirection/overlay/copy operations against the
        // Windows tree or the top-level user/system folders. App subfolders
        // (%LOCALAPPDATA%\App, %ProgramFiles%\App, ...) remain allowed.
        private static bool IsSafeTarget(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return false;
                string full = Path.GetFullPath(path).TrimEnd('\\');
                if (full.Length == 0) return false;
                if (full.StartsWith(@"\\?\") || full.StartsWith(@"\\.\")) return false;
                if (full.Length == 2 && full[1] == ':') return false;

                string userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string usersRootPath = "";
                try
                {
                    if (!string.IsNullOrEmpty(userProfilePath))
                        usersRootPath = Path.GetDirectoryName(userProfilePath) ?? "";
                }
                catch { }

                string[] exact =
                {
                    userProfilePath,
                    usersRootPath,
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                };
                foreach (string candidate in exact)
                {
                    if (string.IsNullOrEmpty(candidate)) continue;
                    string c;
                    try { c = Path.GetFullPath(candidate).TrimEnd('\\'); }
                    catch { continue; }
                    if (string.Equals(full, c, StringComparison.OrdinalIgnoreCase)) return false;
                }

                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (!string.IsNullOrEmpty(windows))
                {
                    windows = Path.GetFullPath(windows).TrimEnd('\\');
                    if (string.Equals(full, windows, StringComparison.OrdinalIgnoreCase) ||
                        full.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                return true;
            }
            catch { return false; }
        }

        private static void Log(string message)
        {
            _logBuffer.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message);
        }

        private static void FlushLog()
        {
            try
            {
                if (!string.IsNullOrEmpty(_logPath))
                {
                    File.AppendAllText(_logPath, _logBuffer.ToString(), Encoding.UTF8);
                }
            }
            catch { }
            _logBuffer.Length = 0;
        }

        private static void Fail(string message)
        {
            Log("ERROR: " + message);
            FlushLog();
            if (!_silent)
            {
                MessageBox.Show(message, "Portable Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    internal sealed class IniFile
    {
        private readonly Dictionary<string, List<KeyValuePair<string, string>>> _sections =
            new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);

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
                    ini.Ensure(section);
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                ini.Ensure(section).Add(new KeyValuePair<string, string>(key, value));
            }
            return ini;
        }

        private List<KeyValuePair<string, string>> Ensure(string section)
        {
            List<KeyValuePair<string, string>> list;
            if (!_sections.TryGetValue(section, out list))
            {
                list = new List<KeyValuePair<string, string>>();
                _sections[section] = list;
            }
            return list;
        }

        public string Get(string section, string key, string defaultValue)
        {
            List<KeyValuePair<string, string>> list;
            if (_sections.TryGetValue(section, out list))
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(list[i].Key, key, StringComparison.OrdinalIgnoreCase))
                        return list[i].Value;
                }
            }
            return defaultValue;
        }

        public int GetInt(string section, string key, int defaultValue)
        {
            int value;
            return int.TryParse(Get(section, key, ""), out value) ? value : defaultValue;
        }

        public bool GetBool(string section, string key, bool defaultValue)
        {
            string value = Get(section, key, "").Trim().ToLowerInvariant();
            if (value == "1" || value == "true" || value == "yes" || value == "on") return true;
            if (value == "0" || value == "false" || value == "no" || value == "off") return false;
            return defaultValue;
        }

        public IEnumerable<KeyValuePair<string, string>> Items(string section)
        {
            List<KeyValuePair<string, string>> list;
            if (_sections.TryGetValue(section, out list)) return list;
            return new List<KeyValuePair<string, string>>();
        }
    }
}
