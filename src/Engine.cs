using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace PortableBuilder
{
    // =====================================================================
    //  Database model
    // =====================================================================

    public sealed class KnownApp
    {
        public string id { get; set; }
        public string name { get; set; }
        public string category { get; set; }
        public string publisher { get; set; }
        public List<string> aliases { get; set; }
        public List<string> tokens { get; set; }
        public List<string> installPaths { get; set; }
        public string exe { get; set; }
        public List<string> dataPaths { get; set; }
        public List<string> registryKeys { get; set; }
        public List<string> processNames { get; set; }
        public List<string> excludes { get; set; }
        public List<string[]> registryReplace { get; set; }
        public string installTarget { get; set; }
        public string args { get; set; }
        public string notes { get; set; }

        [ScriptIgnore] public string NormName;
        [ScriptIgnore] public List<string> NormAliases = new List<string>();
        [ScriptIgnore] public List<string> NormShortAliases = new List<string>();
        [ScriptIgnore] public List<string> NormTokens = new List<string>();

        public void Prepare()
        {
            if (aliases == null) aliases = new List<string>();
            if (tokens == null) tokens = new List<string>();
            if (installPaths == null) installPaths = new List<string>();
            if (dataPaths == null) dataPaths = new List<string>();
            if (registryKeys == null) registryKeys = new List<string>();
            if (processNames == null) processNames = new List<string>();
            if (excludes == null) excludes = new List<string>();
            if (registryReplace == null) registryReplace = new List<string[]>();
            if (installTarget == null) installTarget = "";
            if (exe == null) exe = "";
            if (args == null) args = "";
            if (notes == null) notes = "";
            if (name == null) name = "";
            if (publisher == null) publisher = "";
            if (category == null) category = "";
            if (id == null) id = "";

            NormName = PathUtil.Normalize(name);
            NormAliases.Clear();
            NormShortAliases.Clear();
            foreach (string alias in aliases)
            {
                string n = PathUtil.Normalize(alias);
                if (n.Length >= 5) NormAliases.Add(n);
                else if (n.Length >= 3) NormShortAliases.Add(n);
            }
            NormTokens.Clear();
            foreach (string token in tokens)
            {
                string n = PathUtil.Normalize(token);
                if (n.Length >= 5 && !PathUtil.CommonTokens.Contains(n)) NormTokens.Add(n);
            }
            foreach (string token in PathUtil.WordTokens(name))
            {
                if (token.Length >= 5 && !PathUtil.CommonTokens.Contains(token) && !NormTokens.Contains(token))
                    NormTokens.Add(token);
            }
        }
    }

    public sealed class AppDatabase
    {
        public int version { get; set; }
        public List<KnownApp> apps { get; set; }

        public static AppDatabase Load()
        {
            // Prefer an editable apps.json next to the EXE, but fall back to the
            // embedded copy if the external file is missing or unreadable.
            string externalJson = null;
            try
            {
                string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "apps.json");
                if (File.Exists(file)) externalJson = File.ReadAllText(file, Encoding.UTF8);
            }
            catch { }

            string embeddedJson = null;
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream stream = asm.GetManifestResourceStream("PortableBuilder.apps.json"))
                {
                    if (stream != null)
                    {
                        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                            embeddedJson = reader.ReadToEnd();
                    }
                }
            }
            catch { }

            AppDatabase database = TryParse(externalJson);
            if (database == null || database.apps == null || database.apps.Count == 0)
            {
                database = TryParse(embeddedJson);
            }
            if (database == null)
            {
                database = new AppDatabase { version = 1, apps = new List<KnownApp>() };
            }

            // Optional personal overrides (not embedded; lives next to the EXE).
            string localJson = null;
            try
            {
                string localFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "apps.local.json");
                if (File.Exists(localFile)) localJson = File.ReadAllText(localFile, Encoding.UTF8);
            }
            catch { }
            AppDatabase local = TryParse(localJson);
            if (local != null) database.Merge(local);

            return database;
        }

        // Merges apps from apps.local.json into this database (by id, else by name).
        public void Merge(AppDatabase local)
        {
            if (local == null || local.apps == null) return;
            foreach (KnownApp incoming in local.apps)
            {
                if (incoming == null) continue;
                incoming.Prepare();

                KnownApp existing = FindById(incoming.id);
                if (existing == null)
                {
                    foreach (KnownApp a in apps)
                    {
                        if (a != null && string.Equals(a.name, incoming.name, StringComparison.OrdinalIgnoreCase))
                        {
                            existing = a;
                            break;
                        }
                    }
                }

                if (existing != null)
                {
                    int index = apps.IndexOf(existing);
                    if (index >= 0) apps[index] = incoming;
                    else apps.Add(incoming);
                }
                else
                {
                    apps.Add(incoming);
                }
            }
        }

        private static AppDatabase TryParse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = int.MaxValue;
                AppDatabase db = serializer.Deserialize<AppDatabase>(json);
                if (db == null) db = new AppDatabase();
                if (db.apps == null) db.apps = new List<KnownApp>();

                // Deduplicate by normalized id (keep the first occurrence).
                HashSet<string> seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<KnownApp> unique = new List<KnownApp>();
                foreach (KnownApp app in db.apps)
                {
                    if (app == null) continue;
                    app.Prepare();
                    string id = (app.id ?? "").Trim().ToLowerInvariant();
                    if (id.Length > 0 && !seenIds.Add(id)) continue;
                    unique.Add(app);
                }
                db.apps = unique;
                return db;
            }
            catch
            {
                return null;
            }
        }

        public static string ExternalPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "apps.json");
        }

        public void Save()
        {
            string external = ExternalPath();
            Save(external);

            // Keep the source copy (used by build.cmd) in sync when it sits next to
            // the EXE (i.e. Desktop\PortableBuilder\src\apps.json).
            try
            {
                string src = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "src", "apps.json");
                if (Directory.Exists(Path.GetDirectoryName(src)))
                    Save(src);
            }
            catch { }
        }

        // Adds or updates an entry from a detected app, then persists. Returns the
        // stored entry. Curated metadata (aliases/tokens/publisher/notes) is kept
        // when an existing entry is updated.
        public KnownApp UpsertFromDetected(DetectedApp d, string category, bool persist = true)
        {
            if (d == null) return null;

            KnownApp existing = null;
            if (d.App != null && !string.IsNullOrEmpty(d.App.id) &&
                !d.App.id.StartsWith("unknown-", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(d.App.id, "manual", StringComparison.OrdinalIgnoreCase))
            {
                existing = FindById(d.App.id);
            }
            if (existing == null)
            {
                foreach (KnownApp a in apps)
                    if (string.Equals(a.name, d.Name, StringComparison.OrdinalIgnoreCase)) { existing = a; break; }
            }

            if (existing != null)
            {
                existing.installPaths = new List<string>();
                if (!string.IsNullOrEmpty(d.InstallPath)) existing.installPaths.Add(PathUtil.ToEnv(d.InstallPath));
                if (!string.IsNullOrEmpty(d.ExePath)) existing.exe = Path.GetFileName(d.ExePath);
                existing.dataPaths = new List<string>();
                if (d.DataPaths != null) foreach (string p in d.DataPaths) if (!string.IsNullOrEmpty(p)) existing.dataPaths.Add(PathUtil.ToEnv(p));
                existing.registryKeys = d.RegistryKeys != null ? new List<string>(d.RegistryKeys) : new List<string>();
                if (!string.IsNullOrEmpty(category)) existing.category = category;
                existing.Prepare();
                if (persist) Save();
                return existing;
            }

            KnownApp app = new KnownApp();
            string baseId = PathUtil.Normalize(d.Name);
            app.id = baseId;
            int n = 1;
            while (FindById(app.id) != null) { app.id = baseId + n; n++; }
            app.name = d.Name;
            app.category = string.IsNullOrEmpty(category) ? "Utility" : category;
            app.publisher = d.App != null ? d.App.publisher : "";
            app.aliases = new List<string>();
            app.tokens = PathUtil.WordTokens(d.Name);
            app.installPaths = new List<string>();
            if (!string.IsNullOrEmpty(d.InstallPath)) app.installPaths.Add(PathUtil.ToEnv(d.InstallPath));
            app.exe = string.IsNullOrEmpty(d.ExePath) ? "" : Path.GetFileName(d.ExePath);
            app.dataPaths = new List<string>();
            if (d.DataPaths != null) foreach (string p in d.DataPaths) if (!string.IsNullOrEmpty(p)) app.dataPaths.Add(PathUtil.ToEnv(p));
            app.registryKeys = d.RegistryKeys != null ? new List<string>(d.RegistryKeys) : new List<string>();
            app.processNames = new List<string>();
            app.excludes = d.ExcludePaths != null ? new List<string>(d.ExcludePaths) : new List<string>();
            app.registryReplace = d.RegistryReplace != null ? new List<string[]>(d.RegistryReplace) : new List<string[]>();
            app.installTarget = d.InstallTarget != null ? d.InstallTarget : "";
            app.args = "";
            app.notes = "Added manually on " + DateTime.Now.ToString("yyyy-MM-dd");
            app.Prepare();
            apps.Add(app);
            if (persist) Save();
            return app;
        }

        public void Save(string path)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n  \"version\": 1,\n  \"apps\": [\n");
            for (int i = 0; i < apps.Count; i++)
            {
                sb.Append("    ");
                AppendAppJson(sb, apps[i]);
                sb.Append(i == apps.Count - 1 ? "\n" : ",\n");
            }
            sb.Append("  ]\n}\n");
            WriteAtomic(path, sb.ToString());
        }

        // Writes through a temp file so a crash (or two builders on the same
        // share) can never leave a half-written apps.json behind.
        private static void WriteAtomic(string path, string text)
        {
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            try
            {
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    try
                    {
                        if (File.Exists(path)) File.Replace(temp, path, null);
                        else File.Move(temp, path);
                        return;
                    }
                    catch (IOException)
                    {
                        if (attempt == 3) break;
                        System.Threading.Thread.Sleep(80);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        break;
                    }
                }
            }
            catch { }

            try
            {
                File.Copy(temp, path, true);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        public KnownApp FindById(string id)
        {
            if (string.IsNullOrEmpty(id) || apps == null) return null;
            foreach (KnownApp app in apps)
                if (string.Equals(app.id, id, StringComparison.OrdinalIgnoreCase)) return app;
            return null;
        }

        private static void AppendAppJson(StringBuilder sb, KnownApp a)
        {
            sb.Append("{\n");
            sb.Append("      \"id\": ").Append(JsonStr(a.id)).Append(",\n");
            sb.Append("      \"name\": ").Append(JsonStr(a.name)).Append(",\n");
            sb.Append("      \"category\": ").Append(JsonStr(a.category)).Append(",\n");
            sb.Append("      \"publisher\": ").Append(JsonStr(a.publisher)).Append(",\n");
            sb.Append("      \"aliases\": ").Append(JsonStrList(a.aliases)).Append(",\n");
            sb.Append("      \"tokens\": ").Append(JsonStrList(a.tokens)).Append(",\n");
            sb.Append("      \"installPaths\": ").Append(JsonStrList(a.installPaths)).Append(",\n");
            sb.Append("      \"exe\": ").Append(JsonStr(a.exe)).Append(",\n");
            sb.Append("      \"dataPaths\": ").Append(JsonStrList(a.dataPaths)).Append(",\n");
            sb.Append("      \"registryKeys\": ").Append(JsonStrList(a.registryKeys)).Append(",\n");
            sb.Append("      \"processNames\": ").Append(JsonStrList(a.processNames)).Append(",\n");
            sb.Append("      \"args\": ").Append(JsonStr(a.args)).Append(",\n");
            sb.Append("      \"notes\": ").Append(JsonStr(a.notes)).Append(",\n");
            sb.Append("      \"excludes\": ").Append(JsonStrList(a.excludes)).Append(",\n");
            sb.Append("      \"registryReplace\": ").Append(JsonReplaceList(a.registryReplace)).Append(",\n");
            sb.Append("      \"installTarget\": ").Append(JsonStr(a.installTarget)).Append("\n");
            sb.Append("    }");
        }

        private static string JsonStr(string value)
        {
            if (value == null) return "\"\"";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append("\"");
            return sb.ToString();
        }

        private static string JsonStrList(List<string> list)
        {
            if (list == null || list.Count == 0) return "[]";
            StringBuilder sb = new StringBuilder("[");
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(JsonStr(list[i]));
            }
            sb.Append("]");
            return sb.ToString();
        }

        private static string JsonReplaceList(List<string[]> list)
        {
            if (list == null || list.Count == 0) return "[]";
            StringBuilder sb = new StringBuilder("[");
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                string[] pair = list[i];
                sb.Append("[")
                  .Append(JsonStr(pair != null && pair.Length > 0 ? pair[0] : ""))
                  .Append(", ")
                  .Append(JsonStr(pair != null && pair.Length > 1 ? pair[1] : ""))
                  .Append("]");
            }
            sb.Append("]");
            return sb.ToString();
        }
    }

    // =====================================================================
    //  Detection index (uninstall entries, AppData folders, registry, services)
    // =====================================================================

    public sealed class IndexEntry
    {
        public string Path;
        public string NormLeaf;
        public string NormFull;
        public string NormData;
    }

    public sealed class UninstallEntry
    {
        public string DisplayName;
        public string InstallLocation;
        public string DisplayIcon;
        public string UninstallString;
        public string KeyPath;
        public string NormName;
    }

    public sealed class SystemIndex
    {
        public List<UninstallEntry> Uninstall = new List<UninstallEntry>();
        public List<IndexEntry> DataDirs = new List<IndexEntry>();
        public List<IndexEntry> RegKeys = new List<IndexEntry>();
        public List<IndexEntry> Services = new List<IndexEntry>();

        public static SystemIndex Build(Action<string> log)
        {
            return Build(log, false);
        }

        public static SystemIndex Build(Action<string> log, bool deep)
        {
            SystemIndex idx = new SystemIndex();
            idx.LoadUninstall(log);
            idx.LoadDataDirs(log);
            idx.LoadRegistry(log, deep);
            idx.LoadServices(log);
            if (log != null)
            {
                log("Index (" + (deep ? "deep" : "fast") + "): " + idx.Uninstall.Count + " uninstall entries, " +
                    idx.DataDirs.Count + " data folders, " + idx.RegKeys.Count + " registry keys, " +
                    idx.Services.Count + " services");
            }
            return idx;
        }

        private void LoadUninstall(Action<string> log)
        {
            string[] roots =
            {
                "HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
                "HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
                "HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall"
            };

            foreach (string rootPath in roots)
            {
                try
                {
                    using (RegistryKey root = RegistryUtil.OpenKey(rootPath, false))
                    {
                        if (root == null) continue;
                        foreach (string subName in root.GetSubKeyNames())
                        {
                            try
                            {
                                using (RegistryKey sub = root.OpenSubKey(subName, false))
                                {
                                    if (sub == null) continue;
                                    string displayName = RegistryUtil.ReadString(sub, "DisplayName");
                                    if (string.IsNullOrEmpty(displayName)) continue;

                                    UninstallEntry entry = new UninstallEntry();
                                    entry.DisplayName = displayName;
                                    entry.InstallLocation = RegistryUtil.ReadString(sub, "InstallLocation");
                                    entry.DisplayIcon = RegistryUtil.ReadString(sub, "DisplayIcon");
                                    entry.UninstallString = RegistryUtil.ReadString(sub, "UninstallString");
                                    entry.KeyPath = rootPath + "\\" + subName;
                                    entry.NormName = PathUtil.Normalize(displayName);
                                    Uninstall.Add(entry);
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
        }

        private void LoadDataDirs(Action<string> log)
        {
            string[] roots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow")
            };

            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                try
                {
                    foreach (string top in Directory.EnumerateDirectories(root))
                    {
                        string topName = PathUtil.Normalize(Path.GetFileName(top));
                        if (PathUtil.SharedFolderNames.Contains(topName)) continue;

                        AddDataDir(top);
                        try
                        {
                            foreach (string second in Directory.EnumerateDirectories(top))
                                AddDataDir(second);
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        private void AddDataDir(string path)
        {
            string leaf = Path.GetFileName(path);
            string norm = PathUtil.Normalize(leaf);
            if (norm.Length == 0) return;
            if (PathUtil.SharedFolderNames.Contains(norm)) return;

            IndexEntry entry = new IndexEntry();
            entry.Path = path;
            entry.NormLeaf = norm;
            entry.NormFull = PathUtil.Normalize(path);
            DataDirs.Add(entry);
        }

        private void LoadRegistry(Action<string> log, bool deep)
        {
            if (!deep)
            {
                ScanRegistry("HKEY_CURRENT_USER\\Software", 0, 4, false);
                ScanRegistry("HKEY_LOCAL_MACHINE\\SOFTWARE", 0, 4, false);
                return;
            }

            // Main trees (Microsoft/Classes are skipped here and scanned explicitly below).
            ScanRegistry("HKEY_CURRENT_USER\\Software", 0, 5, true);
            ScanRegistry("HKEY_LOCAL_MACHINE\\SOFTWARE", 0, 5, true);

            // Install / uninstall / app path data that lives under Microsoft.
            ScanRegistry("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall", 0, 4, true);
            ScanRegistry("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall", 0, 4, true);
            ScanRegistry("HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall", 0, 4, true);
            ScanRegistry("HKEY_CURRENT_USER\\Software\\Microsoft\\Windows\\CurrentVersion\\App Paths", 0, 3, true);
            ScanRegistry("HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths", 0, 3, true);
            ScanRegistry("HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\App Paths", 0, 3, true);
            ScanRegistry("HKEY_CURRENT_USER\\Software\\RegisteredApplications", 0, 2, true);
            ScanRegistry("HKEY_LOCAL_MACHINE\\SOFTWARE\\RegisteredApplications", 0, 2, true);

            // Classes (ProgIDs / file associations / shell commands).
            ScanRegistry("HKEY_CURRENT_USER\\Software\\Classes", 0, 3, true);
            ScanRegistry("HKEY_LOCAL_MACHINE\\SOFTWARE\\Classes", 0, 3, true);
            ScanRegistry("HKEY_LOCAL_MACHINE\\SOFTWARE\\WOW6432Node\\Classes", 0, 3, true);

            if (log != null) log("Deep registry scan enabled.");
        }

        private void ScanRegistry(string path, int depth, int maxDepth, bool deep)
        {
            if (depth > maxDepth) return;
            if (RegKeys.Count > (deep ? 150000 : 60000)) return;

            try
            {
                using (RegistryKey key = RegistryUtil.OpenKey(path, false))
                {
                    if (key == null) return;

                    string leaf = Path.GetFileName(path.Replace('\\', '/'));
                    string norm = PathUtil.Normalize(leaf);

                    if (depth > 0 && norm.Length > 2 && !PathUtil.IsRegistryContainer(leaf))
                    {
                        IndexEntry entry = new IndexEntry();
                        entry.Path = path;
                        entry.NormLeaf = norm;
                        entry.NormFull = PathUtil.Normalize(path);
                        entry.NormData = deep ? ReadNormData(key) : "";
                        RegKeys.Add(entry);
                    }

                    if (depth == maxDepth) return;

                    foreach (string child in key.GetSubKeyNames())
                    {
                        if (deep)
                        {
                            string n = PathUtil.Normalize(child);
                            if (n == "microsoft" || n == "classes" || n == "policies") continue;
                        }
                        else if (PathUtil.SkipRegistryBranch(child))
                        {
                            continue;
                        }
                        ScanRegistry(path + "\\" + child, depth + 1, maxDepth, deep);
                    }
                }
            }
            catch { }
        }

        private static string ReadNormData(RegistryKey key)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (string valueName in key.GetValueNames())
                {
                    if (sb.Length > 400) break;
                    sb.Append(' ');
                    sb.Append(valueName);
                    try
                    {
                        object value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                        string s = value as string;
                        if (s != null) { sb.Append(' '); sb.Append(s); }
                        string[] arr = value as string[];
                        if (arr != null)
                        {
                            foreach (string item in arr) { sb.Append(' '); sb.Append(item); }
                        }
                    }
                    catch { }
                }
                string normalized = PathUtil.Normalize(sb.ToString());
                return normalized.Length > 500 ? normalized.Substring(0, 500) : normalized;
            }
            catch { return ""; }
        }

        private void LoadServices(Action<string> log)
        {
            string rootPath = "HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services";
            try
            {
                using (RegistryKey root = RegistryUtil.OpenKey(rootPath, false))
                {
                    if (root == null) return;
                    foreach (string name in root.GetSubKeyNames())
                    {
                        IndexEntry entry = new IndexEntry();
                        entry.Path = rootPath + "\\" + name;
                        entry.NormLeaf = PathUtil.Normalize(name);
                        entry.NormFull = entry.NormLeaf;
                        Services.Add(entry);
                    }
                }
            }
            catch { }
        }
    }

    // =====================================================================
    //  Detected application
    // =====================================================================

    public sealed class DetectedApp
    {
        public KnownApp App;
        public string InstallPath;
        public string ExePath;
        public string DisplayName;
        public string Source;
        public string Notes;
        public List<string> DataPaths = new List<string>();
        public List<string> RegistryKeys = new List<string>();
        public List<string> Services = new List<string>();
        public List<string> ExcludePaths = new List<string>();
        public List<string[]> RegistryReplace = new List<string[]>();
        public string InstallTarget = "";
        public int MatchScore;

        public string Category { get { return App != null && App.category != null ? App.category : ""; } }
        public string Name { get { return App != null && !string.IsNullOrEmpty(App.name) ? App.name : DisplayName; } }
    }

    public sealed class AppDetector
    {
        public static List<DetectedApp> DetectAll(AppDatabase db, SystemIndex index, Action<string> log)
        {
            List<DetectedApp> result = new List<DetectedApp>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (KnownApp app in db.apps)
            {
                if (app == null) continue;
                app.Prepare();

                string installPath = null;
                string source = "";

                foreach (string pattern in app.installPaths)
                {
                    string expanded = PathUtil.Expand(pattern);
                    if (Directory.Exists(expanded)) { installPath = expanded; source = "path"; break; }
                    if (File.Exists(expanded)) { installPath = Path.GetDirectoryName(expanded); source = "path"; break; }
                }

                UninstallEntry uninstall = null;
                if (installPath == null)
                {
                    uninstall = FindUninstall(app, index);
                    if (uninstall != null)
                    {
                        installPath = ResolveUninstallPath(uninstall);
                        source = "uninstall";
                    }
                }

                if (string.IsNullOrEmpty(installPath) || !Directory.Exists(installPath)) continue;
                installPath = Path.GetFullPath(installPath).TrimEnd('\\');

                lock (seen)
                {
                    if (!seen.Add(installPath)) continue;
                }

                string exePath = FindExe(installPath, app.exe, uninstall != null ? uninstall.DisplayIcon : null);
                if (string.IsNullOrEmpty(exePath)) continue;

                DetectedApp detected = Enrich(app, installPath, exePath, index);
                detected.Source = source;
                result.Add(detected);
            }

            result.Sort(delegate(DetectedApp a, DetectedApp b)
            {
                int c = string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            List<DetectedApp> unknown = DetectUnknown(db, index, log);
            if (unknown.Count > 0)
            {
                if (log != null) log("Detected " + unknown.Count + " additional installed program(s) not in the database.");
                result.AddRange(unknown);
            }
            return result;
        }

        // Detects installed programs that are NOT in the database, by scanning the
        // uninstall entries and the top-level Program Files folders.
        public static List<DetectedApp> DetectUnknown(AppDatabase db, SystemIndex index, Action<string> log)
        {
            List<DetectedApp> result = new List<DetectedApp>();
            if (index == null || db == null) return result;

            HashSet<string> claimedInstalls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> claimedUninstall = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (KnownApp app in db.apps)
            {
                if (app == null) continue;
                app.Prepare();
                foreach (string pattern in app.installPaths)
                {
                    string expanded = PathUtil.Expand(pattern);
                    if (Directory.Exists(expanded)) claimedInstalls.Add(Path.GetFullPath(expanded).TrimEnd('\\'));
                    else if (File.Exists(expanded)) claimedInstalls.Add(Path.GetFullPath(Path.GetDirectoryName(expanded)).TrimEnd('\\'));
                }
                UninstallEntry u = FindUninstall(app, index);
                if (u != null)
                {
                    claimedUninstall.Add(u.KeyPath);
                    string p = ResolveUninstallPath(u);
                    if (!string.IsNullOrEmpty(p)) claimedInstalls.Add(Path.GetFullPath(p).TrimEnd('\\'));
                }
            }

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) uninstall entries not claimed by the database
            foreach (UninstallEntry u in index.Uninstall)
            {
                if (u == null || string.IsNullOrEmpty(u.NormName)) continue;
                if (PathUtil.IsBadUninstallDisplayName(u.NormName)) continue;
                if (claimedUninstall.Contains(u.KeyPath)) continue;

                string installPath = ResolveUninstallPath(u);
                if (string.IsNullOrEmpty(installPath) || !Directory.Exists(installPath)) continue;
                installPath = Path.GetFullPath(installPath).TrimEnd('\\');
                if (PathUtil.IsBadInstallPath(installPath)) continue;
                if (claimedInstalls.Contains(installPath)) continue;
                if (!seen.Add(installPath)) continue;

                string exe = FindExe(installPath, "", u.DisplayIcon);
                if (string.IsNullOrEmpty(exe)) continue;

                KnownApp app = MakeProbe(u.DisplayName);
                app.id = "unknown-" + PathUtil.Normalize(u.DisplayName);
                app.category = "Detected";
                DetectedApp d = Enrich(app, installPath, exe, index);
                d.DisplayName = u.DisplayName;
                d.Source = "uninstall";
                d.Notes = "";
                result.Add(d);
            }

            // 2) top-level Program Files folders with no uninstall entry
            string[] pfRoots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };
            foreach (string root in pfRoots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                IEnumerable<string> dirs;
                try { dirs = Directory.EnumerateDirectories(root); }
                catch { continue; }
                foreach (string dir in dirs)
                {
                    string name = Path.GetFileName(dir);
                    string norm = PathUtil.Normalize(name);
                    if (norm.Length < 3) continue;
                    if (IsNoiseFolder(norm)) continue;

                    string full = Path.GetFullPath(dir).TrimEnd('\\');
                    if (claimedInstalls.Contains(full)) continue;
                    if (!seen.Add(full)) continue;

                    string exe = FindExe(full, "", null);
                    if (string.IsNullOrEmpty(exe)) continue;

                    // Show "<App>" instead of "<App>.portable-backup-<date>" for
                    // backups created by earlier portable builds.
                    string displayName = name;
                    int backupIndex = name.IndexOf(".portable-backup", StringComparison.OrdinalIgnoreCase);
                    if (backupIndex > 0)
                    {
                        displayName = name.Substring(0, backupIndex).TrimEnd('.', ' ');
                        if (displayName.Length == 0) displayName = name;
                    }

                    KnownApp app = MakeProbe(displayName);
                    app.id = "unknown-" + PathUtil.Normalize(displayName);
                    app.category = "Detected";
                    DetectedApp d = Enrich(app, full, exe, index);
                    d.DisplayName = displayName;
                    d.Source = "programfiles";
                    d.Notes = "";
                    result.Add(d);
                }
            }

            result.Sort(delegate(DetectedApp a, DetectedApp b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }

        private static bool IsNoiseFolder(string norm)
        {
            if (string.IsNullOrEmpty(norm)) return true;
            if (norm.Contains("windows")) return true;
            if (norm.Contains("microsoft")) return true;
            if (norm.Contains("common")) return true;
            if (norm.Contains("internet")) return true;
            if (norm.Contains("reference")) return true;
            if (norm.Contains("msbuild")) return true;
            if (norm.Contains("wsl")) return true;
            if (norm.Contains("modifiable")) return true;
            if (norm.Contains("package")) return true;
            if (norm.Contains("defender")) return true;
            return false;
        }

        public static DetectedApp Manual(string name, string path, string exePath, SystemIndex index)
        {
            KnownApp app = new KnownApp();
            app.id = "manual";
            app.name = name;
            app.category = "Manual";
            app.aliases = new List<string>();
            app.tokens = PathUtil.WordTokens(name);
            app.installPaths = new List<string>();
            app.exe = "";
            app.dataPaths = new List<string>();
            app.registryKeys = new List<string>();
            app.processNames = new List<string>();
            app.args = "";
            app.notes = "";
            app.Prepare();

            string installPath = path;
            if (File.Exists(path)) installPath = Path.GetDirectoryName(path);
            installPath = Path.GetFullPath(installPath).TrimEnd('\\');

            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                exePath = FindExe(installPath, "", null);

            DetectedApp detected = Enrich(app, installPath, exePath, index);
            detected.Source = "manual";
            detected.DisplayName = name;
            return detected;
        }

        public static DetectedApp Enrich(KnownApp app, string installPath, string exePath, SystemIndex index)
        {
            DetectedApp detected = new DetectedApp();
            detected.App = app;
            detected.InstallPath = installPath;
            detected.ExePath = exePath;
            detected.DisplayName = app.name;
            detected.Notes = app.notes;

            HashSet<string> data = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> regs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> services = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) paths from the database
            foreach (string pattern in app.dataPaths)
            {
                string p = PathUtil.Expand(pattern);
                if (Directory.Exists(p) || File.Exists(p)) data.Add(Path.GetFullPath(p));
            }

            // 2) common per-app folders derived from the install folder name
            string folderName = Path.GetFileName(installPath.TrimEnd('\\'));
            string[] roots =
            {
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow")
            };
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                string p1 = Path.Combine(root, folderName);
                if (Directory.Exists(p1)) data.Add(p1);
                string p2 = Path.Combine(root, app.name);
                if (Directory.Exists(p2)) data.Add(p2);
            }

            // 3) token scan of the pre-built AppData index (only name/alias matches)
            if (index != null)
            {
                foreach (IndexEntry entry in index.DataDirs)
                {
                    if (MatchesName(app, entry.NormLeaf))
                    {
                        if (Directory.Exists(entry.Path)) data.Add(entry.Path);
                    }
                }

                // Fallback for unknown/manual apps: a limited token scan.
                if (data.Count == 0)
                {
                    int added = 0;
                    foreach (IndexEntry entry in index.DataDirs)
                    {
                        if (added >= 5) break;
                        if (PathUtil.SharedFolderNames.Contains(entry.NormLeaf)) continue;
                        if (MatchesToken(app, entry.NormLeaf) && Directory.Exists(entry.Path))
                        {
                            if (data.Add(entry.Path)) added++;
                        }
                    }
                }

                foreach (IndexEntry entry in index.RegKeys)
                {
                    bool match = MatchesName(app, entry.NormLeaf);
                    if (!match && !string.IsNullOrEmpty(entry.NormData))
                    {
                        match = MatchesName(app, entry.NormData) || MatchesToken(app, entry.NormData);
                    }
                    if (match)
                    {
                        if (RegistryUtil.KeyExists(entry.Path)) regs.Add(entry.Path);
                    }
                }

                foreach (IndexEntry entry in index.Services)
                {
                    if (MatchesName(app, entry.NormLeaf) || MatchesToken(app, entry.NormLeaf))
                        services.Add(Path.GetFileName(entry.Path));
                }

                // uninstall entry for this app
                foreach (UninstallEntry u in index.Uninstall)
                {
                    if (MatchesDisplayName(app, u))
                    {
                        if (u.KeyPath.StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase))
                            regs.Add(u.KeyPath);
                    }
                }
            }

            // database registry keys
            foreach (string key in app.registryKeys)
            {
                string expanded = PathUtil.NormalizeRegistryKey(PathUtil.Expand(key));
                if (RegistryUtil.KeyExists(expanded)) regs.Add(expanded);
            }

            // never include the install folder itself as "data"
            string installNorm = Path.GetFullPath(installPath).TrimEnd('\\');
            List<string> dataList = new List<string>();
            foreach (string p in data)
            {
                string full = Path.GetFullPath(p).TrimEnd('\\');
                if (string.Equals(full, installNorm, StringComparison.OrdinalIgnoreCase)) continue;
                if (full.StartsWith(installNorm + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                if (installNorm.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                if (PathUtil.IsDriveRoot(full)) continue;
                dataList.Add(full);
            }
            dataList = PathUtil.RemoveNestedPaths(dataList);

            List<string> regList = new List<string>();
            foreach (string key in regs)
            {
                if (PathUtil.SkipRegistryKey(key)) continue;
                regList.Add(key);
            }
            regList = PathUtil.RemoveNestedRegistryKeys(regList);

            detected.DataPaths = dataList;
            detected.RegistryKeys = regList;
            detected.Services = services.ToList();
            detected.ExcludePaths = new List<string>(app.excludes);
            detected.RegistryReplace = new List<string[]>(app.registryReplace);
            detected.InstallTarget = app.installTarget;
            return detected;
        }

        public static UninstallEntry FindUninstall(KnownApp app, SystemIndex index)
        {
            UninstallEntry best = null;
            int bestScore = -1;

            foreach (UninstallEntry entry in index.Uninstall)
            {
                if (!MatchesDisplayName(app, entry)) continue;
                int score = 0;
                string norm = entry.NormName;
                if (string.Equals(norm, app.NormName, StringComparison.OrdinalIgnoreCase)) score += 100;
                else if (norm.StartsWith(app.NormName, StringComparison.OrdinalIgnoreCase)) score += 60;
                else if (norm.Contains(app.NormName)) score += 40;
                foreach (string alias in app.NormAliases)
                {
                    if (norm.Contains(alias)) { score += 25; break; }
                }
                if (!string.IsNullOrEmpty(ResolveUninstallPath(entry))) score += 10;
                if (!string.IsNullOrEmpty(entry.InstallLocation)) score += 5;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = entry;
                }
            }
            return best;
        }

        private static bool MatchesDisplayName(KnownApp app, UninstallEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.NormName)) return false;
            if (PathUtil.IsBadUninstallDisplayName(entry.NormName)) return false;

            if (app.NormName.Length >= 3 && entry.NormName.Contains(app.NormName)) return true;
            foreach (string alias in app.NormAliases)
            {
                if (entry.NormName.Contains(alias)) return true;
            }
            foreach (string shortAlias in app.NormShortAliases)
            {
                if (string.Equals(entry.NormName, shortAlias, StringComparison.OrdinalIgnoreCase)) return true;
            }
            foreach (string token in app.NormTokens)
            {
                if (entry.NormName.Contains(token)) return true;
            }
            return false;
        }

        public static string ResolveUninstallPath(UninstallEntry entry)
        {
            if (entry == null) return null;

            if (!string.IsNullOrEmpty(entry.InstallLocation))
            {
                string p = PathUtil.CleanPath(entry.InstallLocation);
                if (Directory.Exists(p) && !PathUtil.IsBadInstallPath(p)) return p;
            }

            if (!string.IsNullOrEmpty(entry.DisplayIcon))
            {
                string p = PathUtil.CommandPath(entry.DisplayIcon);
                if (File.Exists(p) && !PathUtil.IsBadInstallPath(p)) return Path.GetDirectoryName(p);
            }

            if (!string.IsNullOrEmpty(entry.UninstallString))
            {
                string p = PathUtil.CommandPath(entry.UninstallString);
                if (File.Exists(p) && !PathUtil.IsBadInstallPath(p)) return Path.GetDirectoryName(p);
            }
            return null;
        }

        public static string FindExe(string installPath, string preferredExe, string displayIcon)
        {
            if (string.IsNullOrEmpty(installPath)) return null;

            if (File.Exists(installPath) && installPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return installPath;

            List<string> exes = PathUtil.SafeEnumerateFiles(installPath, ".exe", 4);
            if (exes.Count == 0) return null;

            string preferred = string.IsNullOrEmpty(preferredExe) ? "" : Path.GetFileName(preferredExe);
            if (preferred.Length > 0)
            {
                List<string> exact = new List<string>();
                foreach (string file in exes)
                {
                    if (string.Equals(Path.GetFileName(file), preferred, StringComparison.OrdinalIgnoreCase))
                        exact.Add(file);
                }
                if (exact.Count > 0)
                {
                    exact.Sort(delegate(string a, string b) { return a.Length.CompareTo(b.Length); });
                    return exact[0];
                }

                // Accept the uninstall DisplayIcon only if it is inside the install folder.
                if (!string.IsNullOrEmpty(displayIcon))
                {
                    string icon = PathUtil.CommandPath(displayIcon);
                    if (File.Exists(icon) && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                        (PathUtil.IsSubPathOf(icon, installPath) || string.Equals(Path.GetDirectoryName(icon), installPath, StringComparison.OrdinalIgnoreCase)))
                        return icon;
                }

                // The expected EXE is missing. Do not pick a random helper/uninstaller.
                return null;
            }

            if (!string.IsNullOrEmpty(displayIcon))
            {
                string icon = PathUtil.CommandPath(displayIcon);
                if (File.Exists(icon) && icon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return icon;
            }

            string best = null;
            long bestSize = -1;
            foreach (string file in exes)
            {
                string name = Path.GetFileName(file).ToLowerInvariant();
                if (PathUtil.IsUninstallerName(name)) continue;
                try
                {
                    long size = new FileInfo(file).Length;
                    if (size > bestSize)
                    {
                        bestSize = size;
                        best = file;
                    }
                }
                catch { }
            }
            return best;
        }

        public static bool MatchesName(KnownApp app, string normalizedText)
        {
            if (string.IsNullOrEmpty(normalizedText)) return false;
            if (app.NormName.Length >= 3 && normalizedText.Contains(app.NormName)) return true;
            foreach (string alias in app.NormAliases)
            {
                if (normalizedText.Contains(alias)) return true;
            }
            foreach (string shortAlias in app.NormShortAliases)
            {
                if (string.Equals(normalizedText, shortAlias, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static bool MatchesToken(KnownApp app, string normalizedText)
        {
            if (string.IsNullOrEmpty(normalizedText)) return false;
            foreach (string token in app.NormTokens)
            {
                if (normalizedText.Contains(token)) return true;
            }
            return false;
        }

        // ---- manual / name-based search ----

        private static KnownApp MakeProbe(string name)
        {
            KnownApp probe = new KnownApp();
            probe.id = "";
            probe.name = name;
            probe.aliases = new List<string>();
            probe.tokens = PathUtil.WordTokens(name);
            probe.installPaths = new List<string>();
            probe.exe = "";
            probe.dataPaths = new List<string>();
            probe.registryKeys = new List<string>();
            probe.processNames = new List<string>();
            probe.excludes = new List<string>();
            probe.registryReplace = new List<string[]>();
            probe.installTarget = "";
            probe.args = "";
            probe.notes = "";
            probe.Prepare();
            return probe;
        }

        private static List<string> SearchTokens(string name)
        {
            List<string> result = new List<string>();
            foreach (string t in PathUtil.WordTokens(name))
            {
                // Generic words ("game", "launcher", "software", ...) must not
                // make every second app a match on their own.
                if (t.Length >= 3 && !PathUtil.CommonTokens.Contains(t) && !result.Contains(t))
                    result.Add(t);
            }
            return result;
        }

        // Ranked matching: exact name/alias beats startswith, which beats
        // all-token containment, which beats a single meaningful token.
        private static int TextScore(string wanted, List<string> tokens, string text)
        {
            if (string.IsNullOrEmpty(wanted) || string.IsNullOrEmpty(text)) return 0;

            if (string.Equals(text, wanted, StringComparison.OrdinalIgnoreCase)) return 1000;
            if (text.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)) return 800;
            if (wanted.Length >= 4 && wanted.StartsWith(text, StringComparison.OrdinalIgnoreCase)) return 700;

            if (tokens == null || tokens.Count == 0)
                return (wanted.Length >= 4 && text.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0) ? 400 : 0;

            int exactTokens = 0;
            int longSubstring = 0;
            foreach (string token in tokens)
            {
                if (string.Equals(text, token, StringComparison.OrdinalIgnoreCase)) exactTokens += 2;
                else if (token.Length >= 4 && text.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    exactTokens++;
                    longSubstring = Math.Max(longSubstring, token.Length);
                }
            }

            if (exactTokens >= tokens.Count) return 600 + tokens.Count * 10;
            if (exactTokens > 0) return 250 + longSubstring * 10;
            return 0;
        }

        private static int AppScore(KnownApp app, string wanted, List<string> tokens)
        {
            int best = TextScore(wanted, tokens, app.NormName);
            foreach (string alias in app.NormAliases)
                best = Math.Max(best, Math.Min(950, TextScore(wanted, tokens, alias)));
            foreach (string alias in app.NormShortAliases)
                best = Math.Max(best, Math.Min(900, TextScore(wanted, tokens, alias)));
            // A token match is useful but must never outrank an exact app name.
            foreach (string token in app.NormTokens)
                best = Math.Max(best, Math.Min(700, TextScore(wanted, tokens, token)));

            // Multi-word queries can be split across name + alias + tokens.
            StringBuilder combined = new StringBuilder();
            combined.Append(app.NormName);
            foreach (string alias in app.NormAliases) combined.Append(alias);
            foreach (string alias in app.NormShortAliases) combined.Append(alias);
            foreach (string token in app.NormTokens) combined.Append(token);
            best = Math.Max(best, Math.Min(650, TextScore(wanted, tokens, combined.ToString())));
            return best;
        }

        public static SearchResult SearchByName(string name, AppDatabase db, SystemIndex index)
        {
            SearchResult result = new SearchResult();
            if (db == null || index == null || string.IsNullOrEmpty(name)) return result;

            string wanted = PathUtil.Normalize(name);
            if (wanted.Length == 0) return result;
            List<string> tokens = SearchTokens(name);

            List<DetectedApp> all = new List<DetectedApp>();
            // With only generic words left there is no meaningful token search;
            // require a strong name/prefix match instead of substring noise.
            int minScore = tokens.Count == 0 ? 700 : 250;
            int hintScore = tokens.Count == 0 ? 700 : 600;
            int dataScore = tokens.Count == 0 ? 1000 : 600;
            HashSet<string> seenHint = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seenData = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seenReg = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> seenSvc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) known database apps whose name/alias/token matches the query
            foreach (KnownApp app in db.apps)
            {
                if (app == null) continue;
                app.Prepare();
                int score = AppScore(app, wanted, tokens);
                if (score < minScore) continue;

                string installPath = null;
                string source = "";
                foreach (string pattern in app.installPaths)
                {
                    string expanded = PathUtil.Expand(pattern);
                    if (Directory.Exists(expanded)) { installPath = expanded; source = "path"; break; }
                    if (File.Exists(expanded)) { installPath = Path.GetDirectoryName(expanded); source = "path"; break; }
                }
                UninstallEntry u = null;
                if (installPath == null)
                {
                    u = FindUninstall(app, index);
                    if (u != null) { installPath = ResolveUninstallPath(u); source = "uninstall"; }
                }
                if (string.IsNullOrEmpty(installPath) || !Directory.Exists(installPath)) continue;
                installPath = Path.GetFullPath(installPath).TrimEnd('\\');

                string exe = FindExe(installPath, app.exe, u != null ? u.DisplayIcon : null);
                if (string.IsNullOrEmpty(exe)) continue;

                DetectedApp d = Enrich(app, installPath, exe, index);
                d.Source = source;
                d.MatchScore = score;
                all.Add(d);
            }

            // 2) uninstall entries matching the query but not covered by the DB
            foreach (UninstallEntry u in index.Uninstall)
            {
                if (string.IsNullOrEmpty(u.NormName)) continue;
                int score = TextScore(wanted, tokens, u.NormName);
                if (score < minScore) continue;

                string installPath = ResolveUninstallPath(u);
                if (string.IsNullOrEmpty(installPath) || !Directory.Exists(installPath)) continue;
                installPath = Path.GetFullPath(installPath).TrimEnd('\\');

                string exe = FindExe(installPath, "", u.DisplayIcon);
                if (string.IsNullOrEmpty(exe)) continue;

                KnownApp app = MakeProbe(u.DisplayName);
                DetectedApp d = Enrich(app, installPath, exe, index);
                d.DisplayName = u.DisplayName;
                d.Source = "uninstall";
                d.MatchScore = score - 50; // a curated DB hit is preferable
                all.Add(d);
            }

            // Highest score first, then alphabetical for stable output.
            all.Sort(delegate(DetectedApp a, DetectedApp b)
            {
                int c = b.MatchScore.CompareTo(a.MatchScore);
                if (c != 0) return c;
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            HashSet<string> seenInstall = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DetectedApp d in all)
            {
                if (string.IsNullOrEmpty(d.InstallPath)) continue;
                if (seenInstall.Add(d.InstallPath)) result.Candidates.Add(d);
            }

            // 3) install hints: uninstall paths with no findable exe (user can pick the exe)
            foreach (UninstallEntry u in index.Uninstall)
            {
                if (string.IsNullOrEmpty(u.NormName)) continue;
                if (TextScore(wanted, tokens, u.NormName) < hintScore) continue;
                string p = ResolveUninstallPath(u);
                if (string.IsNullOrEmpty(p) || !Directory.Exists(p)) continue;
                p = Path.GetFullPath(p).TrimEnd('\\');
                if (seenInstall.Contains(p)) continue;
                if (seenHint.Add(p)) result.InstallHints.Add(p);
            }

            // 4) data folders whose name matches
            foreach (IndexEntry e in index.DataDirs)
            {
                if (TextScore(wanted, tokens, e.NormLeaf) >= dataScore)
                    if (seenData.Add(e.Path)) result.DataFolders.Add(e.Path);
            }

            // 5) registry keys whose name (or value data) matches
            foreach (IndexEntry e in index.RegKeys)
            {
                int score = TextScore(wanted, tokens, e.NormLeaf);
                if (score < dataScore && !string.IsNullOrEmpty(e.NormData))
                    score = TextScore(wanted, tokens, e.NormData);
                if (score >= dataScore && RegistryUtil.KeyExists(e.Path) && seenReg.Add(e.Path))
                    result.RegistryKeys.Add(e.Path);
            }

            // 6) services whose name matches
            foreach (IndexEntry e in index.Services)
            {
                if (TextScore(wanted, tokens, e.NormLeaf) >= dataScore)
                    if (seenSvc.Add(e.Path)) result.Services.Add(Path.GetFileName(e.Path));
            }

            return result;
        }
    }

    public sealed class SearchResult
    {
        public List<DetectedApp> Candidates = new List<DetectedApp>();
        public List<string> InstallHints = new List<string>();
        public List<string> DataFolders = new List<string>();
        public List<string> RegistryKeys = new List<string>();
        public List<string> Services = new List<string>();
    }

    // =====================================================================
    //  Build request / result
    // =====================================================================

    public sealed class BuildRequest
    {
        public string AppName;
        public string AppId;
        public string InstallPath;
        public string InstallTarget = "";
        public string ExePath;
        public string Args = "";
        public string OutputDir;
        public string WaitProcess = "";
        public bool CopyMode;
        public bool ForceSymlink;
        public bool BestLayout;
        public bool IncludeRegistry = true;
        public bool CaptureUpdates;
        public bool CleanPaths;
        public bool WaitForExit = true;
        public bool RestoreOnExit = true;
        public bool EnableRobloxSetup = true;
        public bool EnableGameLoopSetup = true;
        public bool EnableFiveMSetup = true;
        public bool BestJunction;
        public List<string> DataPaths = new List<string>();
        public List<string> RegistryKeys = new List<string>();
        public List<string> Services = new List<string>();
        public List<string> ExcludePaths = new List<string>();
        public List<string[]> RegistryReplace = new List<string[]>();
    }

    public sealed class BuildResult
    {
        public bool Success;
        public string OutputDir;
        public string LauncherPath;
        public int FilesCopied;
        public int RegistryFiles;
        public int Services;
        public string Error;
    }

    // =====================================================================
    //  Targeted builder (known app / manual path)
    // =====================================================================

    public static class TargetedBuilder
    {
        private static BuildResult BuildBestLayout(BuildRequest request, Action<string> log)
        {
            BuildResult result = new BuildResult();
            try
            {
                if (request == null) throw new ArgumentNullException("request");
                if (log == null) log = delegate(string s) { };

                string appName = PathUtil.SafeName(request.AppName);
                string installPath = request.InstallPath;
                if (string.IsNullOrEmpty(installPath)) throw new InvalidOperationException("Install path is required.");
                installPath = Path.GetFullPath(installPath).TrimEnd('\\');
                if (File.Exists(installPath)) installPath = Path.GetDirectoryName(installPath);
                if (!Directory.Exists(installPath)) throw new DirectoryNotFoundException("Install folder not found: " + installPath);

                if (request.EnableRobloxSetup &&
                    string.Equals(request.AppId, "roblox", StringComparison.OrdinalIgnoreCase))
                    return BuildRoblox(request, installPath, request.ExePath, log);
                if (request.EnableGameLoopSetup &&
                    string.Equals(request.AppId, "gameloop", StringComparison.OrdinalIgnoreCase))
                    return BuildGameLoop(request, installPath, request.ExePath, log);
                if (request.EnableFiveMSetup &&
                    string.Equals(request.AppId, "fivem", StringComparison.OrdinalIgnoreCase))
                    return BuildFiveM(request, installPath, request.ExePath, log);

                string exePath = request.ExePath;
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                    throw new FileNotFoundException("Main executable not found: " + exePath);

                string installTarget = request.InstallTarget;
                if (request.CleanPaths && string.IsNullOrEmpty(installTarget) && IsCustomPath(installPath))
                {
                    installTarget = PickCleanTarget(appName, installPath);
                    log("Clean path remap: " + installPath + " -> " + installTarget);
                }

                string linkPath;
                string runExe;
                ResolveInstallTarget(installTarget, installPath, exePath, out linkPath, out runExe);
                List<string[]> registryReplacements = BuildRegistryReplacements(request, installPath, linkPath);

                WarnAbsolutePath(linkPath, log);
                foreach (string rawData in request.DataPaths) WarnAbsolutePath(PathUtil.Expand(rawData), log);

                string outDir = Path.GetFullPath(request.OutputDir);
                if (PathUtil.IsUnsafeOutput(outDir))
                    throw new InvalidOperationException("Refusing to use an unsafe output folder: " + outDir);
                if (request.BestJunction && outDir.StartsWith(@"\\", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Best + Junction needs a local NTFS output folder, not a network share.\r\n" +
                        "Use Best Portable layout (Symlink) for UNC/share outputs.");
                if (PathUtil.IsSameOrSubPath(outDir, installPath) || PathUtil.IsSameOrSubPath(installPath, outDir))
                    throw new InvalidOperationException("Output folder must not overlap the install folder.");
                foreach (string rawData in request.DataPaths)
                {
                    string data = PathUtil.Expand(rawData);
                    if (string.IsNullOrEmpty(data)) continue;
                    try { data = Path.GetFullPath(data); }
                    catch { continue; }
                    if (PathUtil.IsSameOrSubPath(outDir, data) || PathUtil.IsSameOrSubPath(data, outDir))
                        throw new InvalidOperationException("Output folder must not overlap a data folder: " + data);
                }

                PreparePackageDirectory(outDir, appName);

                string dataName = appName + "Data";
                string dataRoot = Path.Combine(outDir, dataName);
                Directory.CreateDirectory(dataRoot);

                List<string> links = new List<string>();
                HashSet<string> usedKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int copied = 0;

                // install folder -> <App>Data\<kind>
                string installKind = UniqueKind(RootKind(linkPath), usedKinds);
                string installRel = Path.Combine(dataName, installKind);
                string installAbs = Path.Combine(outDir, installRel);
                log("Best layout install: " + installPath + " -> " + installRel + "  (link: " + PathUtil.ToEnv(linkPath) + ")");
                int installFailures;
                copied += PathUtil.CopyTree(installPath, installAbs, log, request.ExcludePaths, out installFailures);
                if (installFailures > 0)
                    throw new InvalidOperationException("Install folder copy failed for " + installFailures +
                        " item(s); package was not completed.");
                links.Add(PathUtil.ToEnv(linkPath) + "=" + installRel);

                // data folders
                List<string> dataPaths = PathUtil.NormalizePathList(request.DataPaths);
                dataPaths.Sort(delegate(string a, string b) { return a.Length.CompareTo(b.Length); });
                List<string> acceptedData = new List<string>();
                foreach (string dataPath in dataPaths)
                {
                    string full = Path.GetFullPath(dataPath).TrimEnd('\\');
                    if (PathUtil.IsDriveRoot(full)) continue;
                    if (PathUtil.IsSubPathOf(full, installPath) || PathUtil.IsSubPathOf(installPath, full)) continue;

                    bool covered = false;
                    foreach (string accepted in acceptedData)
                    {
                        if (PathUtil.IsSubPathOf(full, accepted)) { covered = true; break; }
                    }
                    if (covered) continue;

                    if (File.Exists(full))
                    {
                        log("Best layout: file data path skipped (use Normal layout): " + full);
                        continue;
                    }
                    if (!Directory.Exists(full)) continue;

                    string kind = UniqueKind(RootKind(full), usedKinds);
                    string rel = Path.Combine(dataName, kind);
                    string abs = Path.Combine(outDir, rel);
                    log("Best data: " + full + " -> " + rel);
                    int dataFailures;
                    copied += PathUtil.CopyTree(full, abs, log, null, out dataFailures);
                    if (dataFailures > 0)
                        log("Warning: " + dataFailures + " file(s) failed to copy from " + full);
                    links.Add(PathUtil.ToEnv(full) + "=" + rel);
                    acceptedData.Add(full);
                }

                // registry: one merged <App>.reg
                bool hasReg = false;
                string regRel = Path.Combine(dataName, "Regs", appName + ".reg");
                List<string> acceptedKeys = new List<string>();
                if (request.IncludeRegistry && request.RegistryKeys.Count > 0)
                {
                    List<string> keys = new List<string>();
                    HashSet<string> seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string rawKey in request.RegistryKeys)
                    {
                        string normalized = PathUtil.NormalizeRegistryKey(PathUtil.Expand(rawKey));
                        if (normalized.Length == 0) continue;
                        if (PathUtil.SkipRegistryKey(normalized)) continue;
                        if (seenKeys.Add(normalized)) keys.Add(normalized);
                    }
                    keys.Sort(delegate(string a, string b) { return a.Length.CompareTo(b.Length); });

                    string regsDir = Path.Combine(dataRoot, "Regs");
                    Directory.CreateDirectory(regsDir);
                    List<string> tempRegs = new List<string>();
                    int index = 0;

                    foreach (string key in keys)
                    {
                        if (!RegistryUtil.KeyExists(key))
                        {
                            log("Registry key not found, skipped: " + key);
                            continue;
                        }

                        bool covered = false;
                        foreach (string accepted in acceptedKeys)
                        {
                            if (key.StartsWith(accepted + "\\", StringComparison.OrdinalIgnoreCase))
                            {
                                covered = true;
                                break;
                            }
                        }
                        if (covered) continue;

                        string temp = Path.Combine(regsDir, "_tmp_" + index + ".reg");
                        log("Registry: " + key);
                        if (RegistryUtil.ExportKey(key, temp, log))
                        {
                            RegistryUtil.ApplyReplacements(temp, registryReplacements, log);
                            tempRegs.Add(temp);
                            acceptedKeys.Add(key);
                            index++;
                        }
                    }

                    if (tempRegs.Count > 0)
                    {
                        hasReg = MergeRegFiles(tempRegs, Path.Combine(outDir, regRel));
                        foreach (string temp in tempRegs)
                        {
                            try { File.Delete(temp); } catch { }
                        }
                    }
                }

                // services
                List<string> serviceEntries = new List<string>();
                HashSet<string> seenServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string serviceName in request.Services)
                {
                    string name = serviceName.Trim();
                    if (name.Length == 0 || !seenServices.Add(name)) continue;

                    string keyPath = "HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\" + name;
                    string imagePath = RegistryUtil.ReadString(keyPath, "ImagePath");
                    object typeValue = RegistryUtil.ReadValue(keyPath, "Type");
                    int type = typeValue is int ? (int)typeValue : 0;

                    if (string.IsNullOrEmpty(imagePath)) continue;
                    const int SvcKernel = 0x1, SvcFs = 0x2, SvcWin32Own = 0x10, SvcWin32Share = 0x20;
                    bool kernel = (type & (SvcKernel | SvcFs)) != 0;
                    bool userMode = (type & (SvcWin32Own | SvcWin32Share)) != 0;
                    if (kernel || !userMode)
                    {
                        log("Non-user-mode service skipped: " + name);
                        continue;
                    }

                    string display = RegistryUtil.ReadString(keyPath, "DisplayName");
                    string description = RegistryUtil.ReadString(keyPath, "Description");
                    if (string.IsNullOrEmpty(display)) display = name;
                    if (description == null) description = "";

                    string portableImagePath = RemapServiceImagePath(imagePath, installPath, linkPath);

                    serviceEntries.Add(SanitizeServicePart(name) + "=" + SanitizeServicePart(portableImagePath) + "|" +
                        SanitizeServicePart(display) + "|" +
                        SanitizeServicePart(description));
                }

                // launcher.ini (embedded in the generated EXE)
                bool captureUpdates = request.CaptureUpdates && acceptedKeys.Count > 0;
                StringBuilder ini = new StringBuilder();
                ini.AppendLine("; Best Portable layout package generated by PortableBuilder");
                ini.AppendLine("; App: " + appName);
                ini.AppendLine();
                ini.AppendLine("[App]");
                ini.AppendLine("Name=" + appName);
                ini.AppendLine("LinkType=" + (request.BestJunction ? "Junction" : "Symlink"));
                ini.AppendLine("RequireAdmin=" + (request.BestJunction ? "0" : "1"));
                ini.AppendLine("Silent=1");
                ini.AppendLine("LogFile=");
                ini.AppendLine("WaitForExit=" + (captureUpdates || request.WaitForExit ? "1" : "0"));
                ini.AppendLine("RestoreOnExit=" + (request.RestoreOnExit ? "1" : "0"));
                ini.AppendLine("DelayMs=2000");
                ini.AppendLine();

                ini.AppendLine("[Folders]");
                foreach (string line in links) ini.AppendLine(line);
                ini.AppendLine();

                ini.AppendLine("[CopyFolders]");
                ini.AppendLine();
                ini.AppendLine("[Overlay]");
                ini.AppendLine();

                ini.AppendLine("[Registry]");
                if (hasReg) ini.AppendLine("main=" + regRel);
                if (captureUpdates)
                {
                    for (int i = 0; i < acceptedKeys.Count; i++)
                        ini.AppendLine("export" + i + "=" + Path.Combine(dataName, "Regs", "export_" + i + ".reg"));
                }
                ini.AppendLine();

                ini.AppendLine("[RegistryExport]");
                if (captureUpdates)
                {
                    for (int i = 0; i < acceptedKeys.Count; i++)
                        ini.AppendLine(acceptedKeys[i] + "=" + Path.Combine(dataName, "Regs", "export_" + i + ".reg"));
                }
                ini.AppendLine();

                ini.AppendLine("[Services]");
                foreach (string line in serviceEntries) ini.AppendLine(line);
                ini.AppendLine();

                ini.AppendLine("[Run]");
                ini.AppendLine(PathUtil.ToEnv(runExe) + "=" + (request.Args == null ? "" : request.Args));

                string configText = ini.ToString();
                string launcherName = appName + ".exe";
                string iconSource = FindIconSource(exePath, installPath);
                bool compiled = BuildLauncherFromTemplate(configText, outDir, launcherName, iconSource, log);

                if (!compiled)
                    throw new InvalidOperationException("Launcher creation failed; package was not completed.");
                result.LauncherPath = Path.Combine(outDir, launcherName);

                // Generate an uninstaller next to the launcher.
                string uninstallName = "Uninstall_" + appName + ".exe";
                if (BuildLauncherFromTemplate(MakeUninstallConfig(configText), outDir, uninstallName, iconSource, log))
                    log("Uninstaller created: " + uninstallName);

                ValidatePackage(outDir, appName);

                StringBuilder report = new StringBuilder();
                report.AppendLine("PortableBuilder Best Portable layout report");
                report.AppendLine("App: " + appName);
                report.AppendLine("Date: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                report.AppendLine("Install: " + installPath);
                report.AppendLine("Exe: " + exePath);
                report.AppendLine("Layout: " + dataName + "\\...");
                report.AppendLine("Launcher: " + launcherName);
                report.AppendLine("LinkType: Symlink");
                report.AppendLine();
                report.AppendLine("Links (" + links.Count + ")");
                foreach (string line in links) report.AppendLine(line);
                report.AppendLine();
                report.AppendLine("Registry: " + (hasReg ? regRel : "(none)"));
                report.AppendLine("Services (" + serviceEntries.Count + ")");
                foreach (string line in serviceEntries) report.AppendLine(line);
                report.AppendLine("Files copied: " + copied);
                // (changes.txt report intentionally not written: silent builds only)

                result.Success = true;
                result.OutputDir = outDir;
                result.FilesCopied = copied;
                result.RegistryFiles = hasReg ? 1 : 0;
                result.Services = serviceEntries.Count;
                log("DONE (Best Portable layout): " + outDir);
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
                log("ERROR: " + ex);
                return result;
            }
        }

        // -----------------------------------------------------------------
        //  Roblox portable sharing layout
        //  Produces exactly three launchers (run / share / uninstall) plus the
        //  shared game files. No RobloxData, no launcher.ini, no temp files.
        // -----------------------------------------------------------------
        private static BuildResult BuildRoblox(BuildRequest request, string installPath, string sourceExe, Action<string> log)
        {
            BuildResult result = new BuildResult();
            try
            {
                if (log == null) log = delegate(string s) { };

                string outDir = Path.GetFullPath(request.OutputDir);
                if (PathUtil.IsUnsafeOutput(outDir))
                    throw new InvalidOperationException("Refusing to use an unsafe output folder: " + outDir);
                if (request.BestJunction && outDir.StartsWith(@"\\", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Best + Junction needs a local NTFS output folder, not a network share.\r\n" +
                        "Use Best Portable layout (Symlink) for UNC/share outputs.");
                if (PathUtil.IsSameOrSubPath(outDir, installPath) || PathUtil.IsSameOrSubPath(installPath, outDir))
                    throw new InvalidOperationException("Output folder must not overlap the install folder.");

                // Locate the Versions root.
                string versions = Path.Combine(installPath, "Versions");
                string gameRoot = installPath;
                if (!Directory.Exists(versions))
                {
                    string name = Path.GetFileName(installPath);
                    string parent = Path.GetDirectoryName(installPath.TrimEnd('\\'));
                    if (name.StartsWith("version-", StringComparison.OrdinalIgnoreCase) && parent != null)
                    {
                        versions = parent;
                        gameRoot = Path.GetDirectoryName(parent) ?? parent;
                    }
                    else if (string.Equals(name, "Versions", StringComparison.OrdinalIgnoreCase) && parent != null)
                    {
                        // User picked ...\Roblox\Versions: the sibling
                        // Modifications folder lives one level up.
                        versions = installPath;
                        gameRoot = parent;
                    }
                    else
                    {
                        versions = installPath;
                    }
                }
                if (!Directory.Exists(versions))
                    throw new DirectoryNotFoundException("Roblox Versions folder not found: " + versions);

                int versionCount = Directory.GetDirectories(versions, "version-*").Length;
                log("Roblox versions root: " + versions + " (" + versionCount + " version folder(s))");

                PreparePackageDirectory(outDir, "Roblox");

                string outVersions = Path.Combine(outDir, "Versions");
                int copied = 0;
                if (versionCount > 0)
                {
                    if (Directory.Exists(outVersions))
                    {
                        FileAttributes vAttr = File.GetAttributes(outVersions);
                        if ((vAttr & FileAttributes.ReparsePoint) != 0)
                        {
                            Directory.Delete(outVersions, false);
                            log("Removed an old link at: " + outVersions);
                        }
                        else
                        {
                            log("Keeping existing Versions folder and merging: " + outVersions);
                        }
                    }

                    int versionFailures;
                    log("Copying game files: " + versions);
                    copied = PathUtil.CopyTree(versions, outVersions, log, null, out versionFailures);
                    if (versionFailures > 0)
                        throw new InvalidOperationException("Copying the Roblox versions failed for " +
                            versionFailures + " item(s); package was not completed.");
                }
                else
                {
                    // Flat layout: wrap the single client in a version folder.
                    string player = FindRobloxPlayerExe(versions);
                    if (string.IsNullOrEmpty(player))
                        throw new FileNotFoundException("RobloxPlayerBeta.exe not found under " + installPath);
                    string wrapped = Path.Combine(outVersions, "version-local");
                    Directory.CreateDirectory(wrapped);
                    foreach (string file in Directory.GetFiles(versions))
                    {
                        File.Copy(file, Path.Combine(wrapped, Path.GetFileName(file)), true);
                        copied++;
                    }
                    foreach (string dir in Directory.GetDirectories(versions))
                    {
                        string dirName = Path.GetFileName(dir);
                        if (string.Equals(dirName, "Versions", StringComparison.OrdinalIgnoreCase)) continue;
                        int flatFailures;
                        copied += PathUtil.CopyTree(dir, Path.Combine(wrapped, dirName), log, null, out flatFailures);
                        if (flatFailures > 0)
                            throw new InvalidOperationException("Copying " + dirName + " failed for " +
                                flatFailures + " item(s); package was not completed.");
                    }
                    log("Flat Roblox layout wrapped as Versions\\version-local.");
                }

                if (FindRobloxPlayerExe(outVersions) == null)
                    throw new InvalidOperationException(
                        "No RobloxPlayerBeta.exe was found in the copied Versions folder; build aborted.");

                string mods = Path.Combine(gameRoot, "Modifications");
                if (Directory.Exists(mods))
                {
                    string outMods = Path.Combine(outDir, "Modifications");
                    if (Directory.Exists(outMods))
                    {
                        FileAttributes mAttr = File.GetAttributes(outMods);
                        if ((mAttr & FileAttributes.ReparsePoint) != 0)
                        {
                            Directory.Delete(outMods, false);
                            log("Removed an old link at: " + outMods);
                        }
                        else
                        {
                            log("Keeping existing Modifications folder and merging: " + outMods);
                        }
                    }

                    int modFailures;
                    copied += PathUtil.CopyTree(mods, outMods, log, null, out modFailures);
                    if (modFailures > 0)
                        throw new InvalidOperationException("Copying Roblox Modifications failed for " +
                            modFailures + " item(s); package was not completed.");
                }
                else
                {
                    Directory.CreateDirectory(Path.Combine(outDir, "Modifications"));
                }

                // Icon: newest player EXE, then the source EXE, then app.ico.
                string iconSource = FindRobloxPlayerExe(outVersions);
                if (string.IsNullOrEmpty(iconSource)) iconSource = sourceExe;
                iconSource = FindIconSource(iconSource, outDir);

                StringBuilder head = new StringBuilder();
                head.AppendLine("; Roblox Portable layout generated by PortableBuilder");
                head.AppendLine("; Shared game files + per-device data on the same share.");
                head.AppendLine();
                head.AppendLine("[App]");
                head.AppendLine("Name=Roblox Portable");
                head.AppendLine("Silent=0");
                head.AppendLine("LinkType=Symlink");
                head.AppendLine();
                head.AppendLine("[Share]");
                head.AppendLine(@"Root=\\SERVER\Roblox Portable");

                string headText = head.ToString();

                // Mode must live inside [App], not under [Share].
                // Enable_Sharing always needs admin (symlinks); the game and the
                // uninstaller only elevate when the links require it.
                string runConfig = InsertMode(headText, "run", "auto");
                string shareConfig = InsertMode(headText, "share", "auto");
                string uninstallConfig = InsertMode(headText, "uninstall", "auto");

                const string resource = "PortableBuilder.RobloxLauncher.exe";
                string[] names = { "Roblox.exe", "Enable_Sharing.exe", "Uninstall.exe" };
                string[] configs = { runConfig, shareConfig, uninstallConfig };
                string[] modes = { "run", "share", "uninstall" };

                for (int i = 0; i < names.Length; i++)
                {
                    if (!BuildLauncherFromTemplate(configs[i], outDir, names[i], iconSource, resource, log))
                        throw new InvalidOperationException("Could not build " + names[i]);
                    log("Created: " + names[i]);
                }

                for (int i = 0; i < names.Length; i++)
                {
                    string path = Path.Combine(outDir, names[i]);
                    string cfg = ReadAppendedConfigFromFile(path);
                    if (string.IsNullOrEmpty(cfg) || cfg.IndexOf("Mode=" + modes[i], StringComparison.OrdinalIgnoreCase) < 0)
                        throw new InvalidOperationException("Appended config missing or wrong in " + names[i]);
                    VerifyMetadata(path);
                }

                result.Success = true;
                result.OutputDir = outDir;
                result.LauncherPath = Path.Combine(outDir, "Roblox.exe");
                result.FilesCopied = copied;
                log("DONE (Roblox sharing layout): " + outDir);
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
                log("ERROR: " + ex);
                return result;
            }
        }

        // -----------------------------------------------------------------
        //  GameLoop portable layout
        //  Output = the game folder itself (recommended D:\TxGameAssistant).
        //  Generates GameLoop.exe / PUBG Mobile.exe / Apply Gameloop.exe /
        //  Remove Gameloop.exe + Registry\*.reg + Snapshot\ + Temp\.
        // -----------------------------------------------------------------
        private static BuildResult BuildGameLoop(BuildRequest request, string installPath, string sourceExe, Action<string> log)
        {
            BuildResult result = new BuildResult();
            try
            {
                if (log == null) log = delegate(string s) { };

                string root = FindGameLoopRoot(installPath);
                if (string.IsNullOrEmpty(root))
                    throw new InvalidOperationException(
                        "GameLoop folder not found. Expected UI\\AndroidEmulatorEn.exe under:\r\n" +
                        "  " + installPath + "\r\n  D:\\Program Files\\TxGameAssistant\r\n  D:\\TxGameAssistant");

                log("GameLoop root: " + root);

                string outDir = Path.GetFullPath(request.OutputDir);
                if (PathUtil.IsUnsafeOutput(outDir))
                    throw new InvalidOperationException("Refusing to use an unsafe output folder: " + outDir);
                if (request.BestJunction && outDir.StartsWith(@"\\", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Best + Junction needs a local NTFS output folder, not a network share.\r\n" +
                        "Use Best Portable layout (Symlink) for UNC/share outputs.");
                if (PathUtil.IsSameOrSubPath(outDir, root) || PathUtil.IsSameOrSubPath(root, outDir))
                    throw new InvalidOperationException(
                        "Output folder must not overlap the GameLoop source.\r\nSource: " + root + "\r\nOutput: " + outDir);

                if (outDir.StartsWith(@"\\", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "GameLoop must be built to a local folder (D:\\TxGameAssistant), not a network share.\r\n" +
                        "Snapshot and Temp would be shared between PCs if the package lived on a UNC path.");

                if (!string.Equals(outDir.TrimEnd('\\'), @"D:\TxGameAssistant", StringComparison.OrdinalIgnoreCase))
                    log("Note: recommended target folder is D:\\TxGameAssistant. " +
                        "Apply Gameloop.exe patches the registry to the actual folder: " + outDir);

                PreparePackageDirectory(outDir, "GameLoop");
                string[] stale =
                {
                    "GameLoop.exe", "Apply Gameloop.exe", "Remove Gameloop.exe", "PUBG Mobile.exe",
                    "Uninstall_GameLoop.exe", "Uninstall.exe", "launcher.ini", "changes.txt"
                };
                foreach (string name in stale)
                {
                    string path = Path.Combine(outDir, name);
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                }

                log("Copying GameLoop files: " + root);
                int copyFailures;
                int copied = PathUtil.CopyTree(root, outDir, log, request.ExcludePaths, out copyFailures);
                if (copyFailures > 0)
                    throw new InvalidOperationException("Copying the GameLoop files failed for " +
                        copyFailures + " item(s); package was not completed.");

                Directory.CreateDirectory(Path.Combine(outDir, "Snapshot"));
                Directory.CreateDirectory(Path.Combine(outDir, "Temp", "Component"));
                Directory.CreateDirectory(Path.Combine(outDir, "Temp", "MobileGamePCShared"));

                string regDir = Path.Combine(outDir, "Registry");
                Directory.CreateDirectory(regDir);
                ExportRegistryKey(@"HKLM\SOFTWARE\WOW6432Node\Tencent\MobileGamePC",
                    Path.Combine(regDir, "Tencent.reg"), log);
                ExportRegistryKey(@"HKCU\SOFTWARE\Tencent\MobileGamePC",
                    Path.Combine(regDir, "Tencent2.reg"), log);
                ExportRegistryKey(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\MobileGamePC",
                    Path.Combine(regDir, "Uninstall.reg"), log);

                string runExe = FindGameLoopExe(outDir);
                if (string.IsNullOrEmpty(runExe))
                    throw new InvalidOperationException(
                        "GameLoop UI\\AndroidEmulatorEn.exe is missing from the package; build aborted.");
                string appMarket = Path.Combine(outDir, "AppMarket", "AppMarket.exe");
                string runIcon = FindIconSource(runExe, outDir);
                string otherIcon = File.Exists(appMarket) ? FindIconSource(appMarket, outDir) : runIcon;

                string pubgIcon = null;
                string[] pubgIcons =
                {
                    Path.Combine(outDir, "PUBG.ico"), Path.Combine(outDir, "PUBGM.ico"),
                    Path.Combine(outDir, "UI", "PUBG.ico"), Path.Combine(outDir, "UI", "PUBGM.ico")
                };
                foreach (string candidate in pubgIcons)
                {
                    if (File.Exists(candidate)) { pubgIcon = candidate; break; }
                }
                if (string.IsNullOrEmpty(pubgIcon)) pubgIcon = runIcon;

                const string resource = "PortableBuilder.GameLoopLauncher.exe";
                string head =
                    "; GameLoop Portable layout generated by PortableBuilder\r\n" +
                    "; Root = this folder. Snapshot + Temp stay inside the package.\r\n" +
                    "\r\n[App]\r\nName=GameLoop Portable\r\nLinkType=Symlink\r\n\r\n";

                string[] names = { "GameLoop.exe", "PUBG Mobile.exe", "Apply Gameloop.exe", "Remove Gameloop.exe" };
                string[] modes = { "run", "run", "apply", "remove" };
                string[] icons = { runIcon, pubgIcon, otherIcon, otherIcon };

                for (int i = 0; i < names.Length; i++)
                {
                    // Run works as the normal user; only apply/remove need admin.
                    string admin = modes[i] == "run" ? "auto" : "1";
                    string cfg = head + "RequireAdmin=" + admin + "\r\nMode=" + modes[i] + "\r\n";
                    if (!BuildLauncherFromTemplate(cfg, outDir, names[i], icons[i], resource, log))
                        throw new InvalidOperationException("Could not build " + names[i]);
                    log("Created: " + names[i]);
                }

                for (int i = 0; i < names.Length; i++)
                {
                    string path = Path.Combine(outDir, names[i]);
                    string cfg = ReadAppendedConfigFromFile(path);
                    if (string.IsNullOrEmpty(cfg) || cfg.IndexOf("Mode=" + modes[i], StringComparison.OrdinalIgnoreCase) < 0)
                        throw new InvalidOperationException("Appended config missing or wrong in " + names[i]);
                    VerifyMetadata(path);
                }

                result.Success = true;
                result.OutputDir = outDir;
                result.LauncherPath = Path.Combine(outDir, "GameLoop.exe");
                result.FilesCopied = copied;
                log("DONE (GameLoop portable layout): " + outDir);
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
                log("ERROR: " + ex);
                return result;
            }
        }

        // -----------------------------------------------------------------
        //  FiveM portable sharing layout
        //  Package: FiveM.exe / Enable_Sharing.exe / Uninstall.exe + FiveM\ seed.
        //  Runtime: %LOCALAPPDATA%\FiveM is linked per PC to the share, so
        //  caches, server resources, maps and shaders stay in the PC folder.
        // -----------------------------------------------------------------
        private static BuildResult BuildFiveM(BuildRequest request, string installPath, string sourceExe, Action<string> log)
        {
            BuildResult result = new BuildResult();
            try
            {
                if (log == null) log = delegate(string s) { };

                string root = FindFiveMRoot(installPath);
                if (string.IsNullOrEmpty(root))
                    throw new InvalidOperationException(
                        "FiveM folder not found. Expected FiveM.exe + FiveM.app under:\r\n  " +
                        installPath + "\r\n  %LOCALAPPDATA%\\FiveM");

                log("FiveM root: " + root);

                string outDir = Path.GetFullPath(request.OutputDir);
                if (PathUtil.IsUnsafeOutput(outDir))
                    throw new InvalidOperationException("Refusing to use an unsafe output folder: " + outDir);
                if (request.BestJunction && outDir.StartsWith(@"\\", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Best + Junction needs a local NTFS output folder, not a network share.\r\n" +
                        "Use Best Portable layout (Symlink) for UNC/share outputs.");
                if (PathUtil.IsSameOrSubPath(outDir, root) || PathUtil.IsSameOrSubPath(root, outDir))
                    throw new InvalidOperationException(
                        "Output folder must not overlap the FiveM source.\r\nSource: " + root + "\r\nOutput: " + outDir);

                PreparePackageDirectory(outDir, "FiveM");
                string[] stale = { "FiveM.exe", "Enable_Sharing.exe", "Uninstall.exe", "Uninstall_FiveM.exe", "launcher.ini", "changes.txt" };
                foreach (string name in stale)
                {
                    string path = Path.Combine(outDir, name);
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                }

                string seed = Path.Combine(outDir, "FiveM");
                List<string> excludes = new List<string>
                {
                    "FiveM.app\\data",
                    "FiveM.app\\cache",
                    "FiveM.app\\CitizenFX.ini",
                    "CitizenFX.ini"
                };

                // Replace the previous seed so removed app files do not linger.
                PathUtil.DeleteTreeSafe(seed);
                log("Copying the FiveM app (per-PC data folders are excluded): " + root);
                int failed;
                int copied = PathUtil.CopyTree(root, seed, log, excludes, out failed);
                if (failed > 0)
                    throw new InvalidOperationException("Copying the FiveM app failed for " +
                        failed + " item(s); package was not completed.");

                string seedExe = Path.Combine(seed, "FiveM.exe");
                if (!File.Exists(seedExe) || !Directory.Exists(Path.Combine(seed, "FiveM.app")))
                    throw new InvalidOperationException(
                        "The FiveM app seed is incomplete (FiveM.exe / FiveM.app missing).");

                string icon = FindIconSource(seedExe, seed);

                const string resource = "PortableBuilder.FiveMLauncher.exe";
                string head =
                    "; FiveM Portable layout generated by PortableBuilder\r\n" +
                    "; The app stays local; only FiveM.app\\data is linked per PC.\r\n" +
                    "\r\n[App]\r\nName=FiveM Portable\r\nLinkType=Symlink\r\n\r\n" +
                    "[Share]\r\nRoot=\\\\SERVER\\FiveM Portable\r\n";

                string[] names = { "FiveM.exe", "Enable_Sharing.exe", "Uninstall.exe" };
                string[] modes = { "run", "share", "uninstall" };

                for (int i = 0; i < names.Length; i++)
                {
                    // Mode/RequireAdmin must live in [App], not after [Share].
                    string cfg = InsertMode(head, modes[i], "auto");
                    if (!BuildLauncherFromTemplate(cfg, outDir, names[i], icon, resource, log))
                        throw new InvalidOperationException("Could not build " + names[i]);
                    log("Created: " + names[i]);
                }

                for (int i = 0; i < names.Length; i++)
                {
                    string path = Path.Combine(outDir, names[i]);
                    string cfg = ReadAppendedConfigFromFile(path);
                    int app = cfg == null ? -1 : cfg.IndexOf("[App]", StringComparison.OrdinalIgnoreCase);
                    int share = cfg == null ? -1 : cfg.IndexOf("[Share]", StringComparison.OrdinalIgnoreCase);
                    int mode = cfg == null ? -1 : cfg.IndexOf("Mode=" + modes[i], StringComparison.OrdinalIgnoreCase);
                    if (string.IsNullOrEmpty(cfg) || app < 0 || mode < 0 || (share >= 0 && mode > share))
                        throw new InvalidOperationException("Appended config missing or wrong in " + names[i]);
                    VerifyMetadata(path);
                }

                result.Success = true;
                result.OutputDir = outDir;
                result.LauncherPath = Path.Combine(outDir, "FiveM.exe");
                result.FilesCopied = copied;
                log("DONE (FiveM sharing layout): " + outDir);
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
                log("ERROR: " + ex);
                return result;
            }
        }

        private static string FindFiveMRoot(string installPath)
        {
            if (string.IsNullOrEmpty(installPath)) return null;
            string path = Path.GetFullPath(installPath);
            if (File.Exists(path)) path = Path.GetDirectoryName(path);

            List<string> candidates = new List<string>();
            candidates.Add(path);
            string name = Path.GetFileName(path.TrimEnd('\\'));
            if (string.Equals(name, "FiveM.app", StringComparison.OrdinalIgnoreCase))
            {
                string parent = Path.GetDirectoryName(path.TrimEnd('\\'));
                if (!string.IsNullOrEmpty(parent)) candidates.Add(parent);
            }
            candidates.Add(Path.Combine(path, "FiveM"));
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FiveM"));

            foreach (string candidate in candidates)
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                if (File.Exists(Path.Combine(candidate, "FiveM.exe")) &&
                    Directory.Exists(Path.Combine(candidate, "FiveM.app")))
                    return Path.GetFullPath(candidate).TrimEnd('\\');
            }
            return null;
        }

        private static string FindGameLoopRoot(string installPath)
        {
            if (string.IsNullOrEmpty(installPath)) return null;
            string path = Path.GetFullPath(installPath);

            List<string> candidates = new List<string>();
            candidates.Add(path);
            candidates.Add(Path.Combine(path, "TxGameAssistant"));
            string name = Path.GetFileName(path.TrimEnd('\\'));
            if (string.Equals(name, "UI", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "AppMarket", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "AOW_Rootfs_100", StringComparison.OrdinalIgnoreCase))
            {
                string parent = Path.GetDirectoryName(path.TrimEnd('\\'));
                if (!string.IsNullOrEmpty(parent)) candidates.Add(parent);
            }

            candidates.Add(@"D:\Program Files\TxGameAssistant");
            candidates.Add(@"D:\TxGameAssistant");

            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(pf))
                candidates.Add(Path.Combine(pf, "TxGameAssistant"));
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(pf86))
                candidates.Add(Path.Combine(pf86, "TxGameAssistant"));

            foreach (string dir in candidates)
                if (!string.IsNullOrEmpty(dir) && FindGameLoopExe(dir) != null) return Path.GetFullPath(dir).TrimEnd('\\');
            return null;
        }

        private static string FindGameLoopExe(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
            string[] dirs = { "UI", "ui", "Ui" };
            foreach (string dir in dirs)
            {
                string path = Path.Combine(root, dir, "AndroidEmulatorEn.exe");
                if (File.Exists(path)) return path;
            }
            return null;
        }

        private static void ExportRegistryKey(string key, string file, Action<string> log)
        {
            if (RegistryUtil.ExportKey(key, file, log))
                log("Registry exported: " + Path.GetFileName(file));
            else
                log("Registry export skipped: " + key);
        }

        private static string InsertMode(string configHead, string mode, string requireAdmin)
        {
            int index = configHead.IndexOf("[App]", StringComparison.OrdinalIgnoreCase);
            if (index < 0) return configHead + "RequireAdmin=" + requireAdmin + "\r\nMode=" + mode + "\r\n";
            int lineEnd = configHead.IndexOf('\n', index);
            if (lineEnd < 0) lineEnd = configHead.Length;
            return configHead.Substring(0, lineEnd + 1) +
                   "RequireAdmin=" + requireAdmin + "\r\n" +
                   "Mode=" + mode + "\r\n" +
                   configHead.Substring(lineEnd + 1);
        }

        private static string FindRobloxPlayerExe(string root)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;

            List<string> dirs = new List<string>();
            dirs.Add(root);
            try
            {
                foreach (string dir in Directory.GetDirectories(root))
                    if (Path.GetFileName(dir).StartsWith("version-", StringComparison.OrdinalIgnoreCase))
                        dirs.Add(dir);
            }
            catch { }

            foreach (string dir in dirs)
            {
                string player = Path.Combine(dir, "RobloxPlayerBeta.exe");
                if (File.Exists(player)) return player;
            }
            foreach (string dir in dirs)
            {
                string launcher = Path.Combine(dir, "RobloxPlayerLauncher.exe");
                if (File.Exists(launcher)) return launcher;
            }
            try
            {
                foreach (string file in Directory.GetFiles(root, "Roblox*.exe", SearchOption.AllDirectories))
                    return file;
            }
            catch { }
            return null;
        }

        private static string SanitizeServicePart(string value)
        {
            if (value == null) return "";
            return value.Replace("\r", " ").Replace("\n", " ").Replace("|", " ").Replace("=", " ");
        }

        // Remaps only the executable part of a service ImagePath. The raw value
        // may be quoted and/or carry arguments ("...\svc.exe" -svc).
        private static string RemapServiceImagePath(string imagePath, string installPath, string linkPath)
        {
            if (string.IsNullOrEmpty(imagePath)) return imagePath;
            if (string.Equals(linkPath, installPath, StringComparison.OrdinalIgnoreCase)) return imagePath;

            string trimmed = imagePath.Trim();
            if (trimmed.Length == 0) return imagePath;

            string exe;
            string suffix;
            bool quoted = trimmed[0] == '"';
            if (quoted)
            {
                int close = trimmed.IndexOf('"', 1);
                if (close < 0) return imagePath;
                exe = trimmed.Substring(1, close - 1);
                suffix = trimmed.Substring(close + 1);
            }
            else
            {
                int space = trimmed.IndexOf(' ');
                exe = space < 0 ? trimmed : trimmed.Substring(0, space);
                suffix = space < 0 ? "" : trimmed.Substring(space);
            }

            if (!string.Equals(exe, installPath, StringComparison.OrdinalIgnoreCase) &&
                !exe.StartsWith(installPath + "\\", StringComparison.OrdinalIgnoreCase))
                return imagePath;

            string newExe = linkPath + exe.Substring(installPath.Length);
            return (quoted ? "\"" + newExe + "\"" : newExe) + suffix;
        }

        private static void ResolveInstallTarget(string installTarget, string installPath, string exePath,
            out string linkPath, out string runExe)
        {
            linkPath = installPath;
            if (!string.IsNullOrEmpty(installTarget))
            {
                try { linkPath = Path.GetFullPath(PathUtil.Expand(installTarget)); }
                catch { linkPath = installPath; }
            }

            runExe = exePath;
            if (!string.Equals(linkPath, installPath, StringComparison.OrdinalIgnoreCase))
            {
                string rel = PathUtil.IsSubPathOf(exePath, installPath)
                    ? exePath.Substring(installPath.Length).TrimStart('\\')
                    : Path.GetFileName(exePath);
                runExe = Path.Combine(linkPath, rel);
            }
        }

        // True when the install path cannot be expressed with a standard env var
        // (e.g. D:\Programs\..., E:\...). These paths would otherwise be embedded
        // verbatim into the generated launcher config.
        private static bool IsCustomPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string env = PathUtil.ToEnv(path);
            return string.Equals(env, path, StringComparison.OrdinalIgnoreCase) && Path.IsPathRooted(env);
        }

        // Remaps a custom path to a standard, relocatable location under Program Files.
        private static string PickCleanTarget(string appName, string installPath)
        {
            string safe = PathUtil.SafeName(appName);
            bool x86 = installPath != null &&
                installPath.IndexOf("(x86)", StringComparison.OrdinalIgnoreCase) >= 0;
            return (x86 ? "%ProgramFiles(x86)%\\" : "%ProgramFiles%\\") + safe;
        }

        private static List<string[]> BuildRegistryReplacements(BuildRequest request, string installPath, string linkPath)
        {
            List<string[]> list = request.RegistryReplace != null
                ? new List<string[]>(request.RegistryReplace)
                : new List<string[]>();
            if (!string.Equals(linkPath, installPath, StringComparison.OrdinalIgnoreCase))
                list.Add(new string[] { installPath, linkPath });
            return list;
        }

        private static void WarnAbsolutePath(string path, Action<string> log)
        {
            if (log == null || string.IsNullOrEmpty(path)) return;

            string env = PathUtil.ToEnv(path);
            if (env.StartsWith("%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase) ||
                env.StartsWith("%APPDATA%", StringComparison.OrdinalIgnoreCase) ||
                env.StartsWith("%ProgramData%", StringComparison.OrdinalIgnoreCase) ||
                env.StartsWith("%ProgramFiles%", StringComparison.OrdinalIgnoreCase) ||
                env.StartsWith("%ProgramFiles(x86)%", StringComparison.OrdinalIgnoreCase) ||
                env.StartsWith("%USERPROFILE%", StringComparison.OrdinalIgnoreCase))
                return;

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile) && path.StartsWith(profile + "\\", StringComparison.OrdinalIgnoreCase))
                return;

            log("[!] Absolute path: " + path + " — on another PC without this drive/path it will fail.");
        }

        private static string RootKind(string path)        {
            string env = PathUtil.ToEnv(path);
            // LocalLow must be checked before Local: "...\AppData\LocalLow"
            // also starts with "...\AppData\Local".
            if (env.StartsWith("%USERPROFILE%\\AppData\\LocalLow", StringComparison.OrdinalIgnoreCase)) return "AppData\\LocalLow";
            if (env.StartsWith("%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase)) return "AppData\\Local";
            if (env.StartsWith("%APPDATA%", StringComparison.OrdinalIgnoreCase)) return "AppData\\Roaming";
            if (env.StartsWith("%ProgramData%", StringComparison.OrdinalIgnoreCase)) return "ProgramData";
            if (env.StartsWith("%ProgramFiles(x86)%", StringComparison.OrdinalIgnoreCase)) return "ProgramFiles(x86)";
            if (env.StartsWith("%ProgramFiles%", StringComparison.OrdinalIgnoreCase)) return "ProgramFiles";

            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile) && path.StartsWith(profile + "\\", StringComparison.OrdinalIgnoreCase))
                return "UserProfile";

            return "Install";
        }

        private static string UniqueKind(string kind, HashSet<string> used)
        {
            if (used.Add(kind)) return kind;

            // Use the sibling Program Files name instead of "_2" when the same
            // root is used twice (e.g. Steam client + Common Files\Steam).
            string alternate = null;
            if (string.Equals(kind, "ProgramFiles(x86)", StringComparison.OrdinalIgnoreCase))
                alternate = "ProgramFiles";
            else if (string.Equals(kind, "ProgramFiles", StringComparison.OrdinalIgnoreCase))
                alternate = "ProgramFiles(x86)";

            if (alternate != null)
            {
                if (used.Add(alternate)) return alternate;
            }

            int index = 2;
            while (!used.Add(kind + "_" + index)) index++;
            return kind + "_" + index;
        }

        private static bool MergeRegFiles(List<string> files, string output)
        {
            try
            {
                StringBuilder body = new StringBuilder();
                foreach (string file in files)
                {
                    string[] lines = File.ReadAllLines(file, Encoding.Unicode);
                    bool started = false;
                    foreach (string line in lines)
                    {
                        if (!started)
                        {
                            if (line.TrimStart().StartsWith("[")) started = true;
                            else continue;
                        }
                        body.AppendLine(line);
                    }
                }

                if (body.Length == 0) return false;
                string text = "Windows Registry Editor Version 5.00\r\n\r\n" + body.ToString();
                string parent = Path.GetDirectoryName(output);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                File.WriteAllText(output, text, Encoding.Unicode);
                return true;
            }
            catch { return false; }
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern uint PrivateExtractIcons(string szFileName, int nIconIndex, int cxIcon, int cyIcon,
            IntPtr[] phicon, uint[] piconid, uint nIcons, uint flags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr BeginUpdateResource(string pFileName, bool bDeleteExistingResources);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateResource(IntPtr hUpdate, IntPtr lpType, IntPtr lpName, ushort wLanguage, byte[] lpData, uint cbData);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool EndUpdateResource(IntPtr hUpdate, bool fDiscard);
        private static readonly IntPtr RT_ICON = (IntPtr)3;
        private static readonly IntPtr RT_GROUP_ICON = (IntPtr)14;

        // Extract several native icon sizes (256/48/32/16) and write a proper
        // multi-image .ico (PNG for 256, 32-bit BMP for smaller sizes).
        // Icon.Save() corrupts large/BMP icons, so we render and encode ourselves.
        private static bool ExtractBestIcon(string sourceExe, string outIco)
        {
            int[] sizes = { 256, 48, 32, 16 };
            List<int> widths = new List<int>();
            List<int> heights = new List<int>();
            List<byte[]> images = new List<byte[]>();

            foreach (int size in sizes)
            {
                IntPtr[] icons = new IntPtr[1];
                uint[] ids = new uint[1];
                try
                {
                    uint count = PrivateExtractIcons(sourceExe, 0, size, size, icons, ids, 1, 0);
                    if (count > 0 && icons[0] != IntPtr.Zero)
                    {
                        using (Bitmap bmp = Bitmap.FromHicon(icons[0]))
                        {
                            images.Add(EncodeIconImage(bmp, size >= 256));
                            widths.Add(bmp.Width);
                            heights.Add(bmp.Height);
                        }
                        DestroyIcon(icons[0]);
                    }
                }
                catch { if (icons[0] != IntPtr.Zero) DestroyIcon(icons[0]); }
            }

            // Fallback: the classic associated icon.
            if (images.Count == 0)
            {
                try
                {
                    using (Icon icon = Icon.ExtractAssociatedIcon(sourceExe))
                    {
                        if (icon != null)
                        {
                            using (Bitmap bmp = icon.ToBitmap())
                            {
                                images.Add(EncodeIconImage(bmp, false));
                                widths.Add(bmp.Width);
                                heights.Add(bmp.Height);
                            }
                        }
                    }
                }
                catch { }
            }

            if (images.Count == 0) return false;
            return WriteIco(outIco, widths, heights, images);
        }

        private static byte[] EncodeIconImage(Bitmap bmp, bool preferPng)
        {
            if (preferPng)
            {
                using (MemoryStream png = new MemoryStream())
                {
                    bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                    return png.ToArray();
                }
            }
            return BitmapToDib(bmp);
        }

        // 32-bit ICO DIB: BITMAPINFOHEADER + BGRA pixels (bottom-up) + AND mask.
        private static byte[] BitmapToDib(Bitmap bmp)
        {
            int w = bmp.Width;
            int h = bmp.Height;
            int maskStride = ((w + 31) / 32) * 4;
            int xorSize = w * h * 4;
            int maskSize = maskStride * h;

            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write(40);                    // biSize
                bw.Write(w);                     // biWidth
                bw.Write(h * 2);                 // biHeight (XOR + AND)
                bw.Write((ushort)1);             // biPlanes
                bw.Write((ushort)32);            // biBitCount
                bw.Write(0);                     // biCompression (BI_RGB)
                bw.Write(xorSize + maskSize);    // biSizeImage
                bw.Write(0); bw.Write(0);        // pels per meter
                bw.Write(0); bw.Write(0);        // clr used / important

                for (int y = h - 1; y >= 0; y--)
                {
                    for (int x = 0; x < w; x++)
                    {
                        Color c = bmp.GetPixel(x, y);
                        bw.Write(c.B);
                        bw.Write(c.G);
                        bw.Write(c.R);
                        bw.Write(c.A);
                    }
                }

                bw.Write(new byte[maskSize]);
                bw.Flush();
                return ms.ToArray();
            }
        }

        private static bool WriteIco(string path, List<int> widths, List<int> heights, List<byte[]> images)
        {
            try
            {
                int count = images.Count;
                using (FileStream fs = File.Create(path))
                using (BinaryWriter writer = new BinaryWriter(fs))
                {
                    writer.Write((ushort)0);   // reserved
                    writer.Write((ushort)1);   // type: icon
                    writer.Write((ushort)count);

                    int offset = 6 + 16 * count;
                    for (int i = 0; i < count; i++)
                    {
                        writer.Write((byte)(widths[i] >= 256 ? 0 : widths[i]));
                        writer.Write((byte)(heights[i] >= 256 ? 0 : heights[i]));
                        writer.Write((byte)0);       // color count
                        writer.Write((byte)0);       // reserved
                        writer.Write((ushort)1);     // planes
                        writer.Write((ushort)32);    // bit count
                        writer.Write((uint)images[i].Length);
                        writer.Write((uint)offset);
                        offset += images[i].Length;
                    }

                    foreach (byte[] image in images)
                        writer.Write(image);
                }
                return true;
            }
            catch { return false; }
        }

        // Version metadata for the generated <App>.exe (Explorer "Details" tab).
        private static string BuildAssemblyInfo(string appLabel)
        {
            string title = EscapeCs(appLabel);
            return
                "using System.Reflection;\r\n" +
                "using System.Runtime.InteropServices;\r\n" +
                "[assembly: AssemblyTitle(\"" + title + "\")]\r\n" +
                "[assembly: AssemblyDescription(\"" + title + "\")]\r\n" +
                "[assembly: AssemblyProduct(\"" + title + "\")]\r\n" +
                "[assembly: AssemblyCompany(\"Cyber Misr | \\u0633\\u0627\\u064A\\u0628\\u0631 \\u0645\\u0635\\u0631\")]\r\n" +
                "[assembly: AssemblyCopyright(\"\")]\r\n" +
                "[assembly: AssemblyFileVersion(\"1.1.0.0\")]\r\n" +
                "[assembly: AssemblyVersion(\"1.1.0.0\")]\r\n" +
                "[assembly: AssemblyInformationalVersion(\"1.1.0.0\")]\r\n" +
                "[assembly: ComVisible(false)]\r\n";
        }

        private static string EscapeCs(string value)
        {
            if (value == null) return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // Turns the launcher config into an uninstaller config: Uninstall=1,
        // Silent=0 (show confirmation), and no [Run] section.
        private static string MakeUninstallConfig(string configText)
        {
            string text = configText.Replace("Silent=1", "Silent=0");
            text = text.Replace("Silent=0\r\n", "Silent=0\r\nUninstall=1\r\n");

            // Fallback for configs that use LF-only line endings.
            if (text.IndexOf("Uninstall=1", StringComparison.OrdinalIgnoreCase) < 0)
            {
                int app = text.IndexOf("[App]", StringComparison.OrdinalIgnoreCase);
                if (app >= 0)
                {
                    int lineEnd = text.IndexOf('\n', app);
                    if (lineEnd < 0) lineEnd = text.Length - 1;
                    text = text.Substring(0, lineEnd + 1) +
                           "Uninstall=1\r\n" +
                           text.Substring(lineEnd + 1);
                }
                else
                {
                    text = "[App]\r\nUninstall=1\r\n" + text;
                }
            }

            int run = text.IndexOf("[Run]", StringComparison.OrdinalIgnoreCase);
            if (run >= 0) text = text.Substring(0, run);
            return text;
        }

        // Deletes only package-owned artifacts; never unrelated user files in the
        // chosen output folder (audit: destructive recursive delete).
        private static void PreparePackageDirectory(string outDir, string appName)
        {
            if (Directory.Exists(outDir))
            {
                FileAttributes attr = File.GetAttributes(outDir);
                if ((attr & FileAttributes.ReparsePoint) != 0) Directory.Delete(outDir, false);
            }
            Directory.CreateDirectory(outDir);

            string data = Path.Combine(outDir, appName + "Data");
            if (Directory.Exists(data))
            {
                FileAttributes dataAttr = File.GetAttributes(data);
                if ((dataAttr & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(data, false); // remove the link, never its target
                else
                    PathUtil.DeleteTreeSafe(data);
            }

            foreach (string file in Directory.GetFiles(outDir))
            {
                string name = Path.GetFileName(file);
                if (name.Equals(appName + ".exe", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Uninstall_" + appName + ".exe", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("PUBG Mobile.exe", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("launcher.ini", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("changes.txt", StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }

        // Reads the appended config from a packaged EXE (for validation).
        private static string ReadAppendedConfigFromFile(string exePath)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(exePath);
                byte[] marker = Encoding.ASCII.GetBytes(";;PB2CFG;;");
                byte[] endm = Encoding.ASCII.GetBytes(";;PB2END;;");
                int start = -1;
                for (int i = bytes.Length - marker.Length; i >= 0; i--) { if (BytesMatch(bytes, marker, i)) { start = i; break; } }
                if (start < 0) return null;
                start += marker.Length;
                int end = -1;
                for (int i = start; i <= bytes.Length - endm.Length; i++) { if (BytesMatch(bytes, endm, i)) { end = i; break; } }
                if (end < 0) return null;
                string b64 = Encoding.ASCII.GetString(bytes, start, end - start).Trim();
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

        // Validates that the package is complete and self-contained.
        private static void ValidatePackage(string outDir, string appName)
        {
            string exe = Path.Combine(outDir, appName + ".exe");
            string uninstall = Path.Combine(outDir, "Uninstall_" + appName + ".exe");
            string data = Path.Combine(outDir, appName + "Data");
            if (!Directory.Exists(data)) data = Path.Combine(outDir, "Data");

            if (!File.Exists(exe)) throw new InvalidOperationException("Launcher missing: " + exe);
            if (!File.Exists(uninstall)) throw new InvalidOperationException("Uninstaller missing: " + uninstall);
            if (!Directory.Exists(data)) throw new InvalidOperationException("Data folder missing: " + data);
            if (Directory.GetFileSystemEntries(data).Length == 0)
                throw new InvalidOperationException("Data folder is empty: " + data);

            string cfg = ReadAppendedConfigFromFile(exe);
            if (string.IsNullOrEmpty(cfg))
                throw new InvalidOperationException("Appended config missing in " + exe);

            string uninstallCfg = ReadAppendedConfigFromFile(uninstall);
            if (string.IsNullOrEmpty(uninstallCfg))
                throw new InvalidOperationException("Appended config missing in " + uninstall);
            if (uninstallCfg.IndexOf("Uninstall=1", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("Uninstaller config is invalid: " + uninstall);

            VerifyMetadata(exe);
            VerifyMetadata(uninstall);
        }

        private static void VerifyMetadata(string exePath)
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(exePath);
            const string company = "Cyber Misr | \u0633\u0627\u064A\u0628\u0631 \u0645\u0635\u0631";
            if (!string.Equals(info.CompanyName, company, StringComparison.Ordinal))
                throw new InvalidOperationException("Company metadata incorrect: " + exePath);

            Version current = Assembly.GetExecutingAssembly().GetName().Version;
            string expected = current == null ? "1.1.0.0" : current.ToString();
            if (string.IsNullOrEmpty(info.FileVersion) ||
                !info.FileVersion.StartsWith(expected, StringComparison.Ordinal))
                throw new InvalidOperationException("File version metadata incorrect: " + exePath +
                    " (expected " + expected + ")");
        }

        // Temp-free packaging: copy the embedded launcher, patch the app icon,
        // then append the config to the EXE itself.
        private static bool BuildLauncherFromTemplate(string configText, string outDir, string exeName, string iconSource, Action<string> log)
        {
            return BuildLauncherFromTemplate(configText, outDir, exeName, iconSource,
                "PortableBuilder.PortableLauncher.exe", log);
        }

        private static bool BuildLauncherFromTemplate(string configText, string outDir, string exeName, string iconSource,
            string resourceName, Action<string> log)
        {
            try
            {
                string outExe = Path.Combine(outDir, exeName);
                if (File.Exists(outExe)) File.Delete(outExe);

                if (!PathUtil.ExtractEmbeddedResource(resourceName, outExe))
                {
                    log("Embedded launcher not found: " + resourceName);
                    return false;
                }

                byte[] ico = null;
                bool embeddedIcon = false;
                try
                {
                    if (!string.IsNullOrEmpty(iconSource) &&
                        iconSource.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(iconSource))
                    {
                        byte[] raw = File.ReadAllBytes(iconSource);
                        if (IsValidIco(raw)) ico = raw;
                    }
                    else
                    {
                        ico = ExtractIconBytes(iconSource);
                    }
                }
                catch { ico = null; }

                if (ico == null)
                {
                    ico = LoadEmbeddedIcon();
                    embeddedIcon = true;
                }
                if (ico == null)
                {
                    log("Icon missing; build aborted.");
                    return false;
                }
                if (!PatchIcon(outExe, ico))
                {
                    // A broken/unsupported sidecar icon should not break the
                    // whole build: retry once with the embedded app icon.
                    bool patched = false;
                    if (!embeddedIcon)
                    {
                        byte[] fallback = LoadEmbeddedIcon();
                        if (fallback != null) patched = PatchIcon(outExe, fallback);
                    }
                    if (!patched)
                    {
                        log("Icon patch failed; build aborted.");
                        return false;
                    }
                    log("Icon patch used the embedded fallback: " + exeName);
                }

                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(configText));
                using (FileStream fs = new FileStream(outExe, FileMode.Append, FileAccess.Write))
                using (StreamWriter writer = new StreamWriter(fs, new UTF8Encoding(false)))
                {
                    writer.Write(";;PB2CFG;;" + b64 + ";;PB2END;;");
                }
                return true;
            }
            catch (Exception ex)
            {
                log("Launcher template failed: " + ex.Message);
                return false;
            }
        }

        private static byte[] LoadEmbeddedIcon()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream stream = asm.GetManifestResourceStream("PortableBuilder.app.ico"))
                {
                    if (stream == null) return null;
                    using (MemoryStream ms = new MemoryStream())
                    {
                        stream.CopyTo(ms);
                        return ms.ToArray();
                    }
                }
            }
            catch { return null; }
        }

        // Prefer the main EXE icon; if it has none, look for a sidecar .ico
        // (app.ico / icon.ico / logo.ico / <exe>.ico) so more apps keep their
        // real icon in the generated launcher.
        private static string FindIconSource(string exePath, string installPath)
        {
            try
            {
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath) &&
                    ExtractIconBytes(exePath) != null)
                    return exePath;
            }
            catch { }

            List<string> dirs = new List<string>();
            if (!string.IsNullOrEmpty(exePath))
            {
                string dir = Path.GetDirectoryName(exePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dirs.Add(dir);
            }
            if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath) &&
                !dirs.Contains(installPath))
                dirs.Add(installPath);

            string baseName = string.IsNullOrEmpty(exePath) ? "" : Path.GetFileNameWithoutExtension(exePath);
            string[] names =
            {
                "app.ico", "icon.ico", "logo.ico", "appicon.ico", "application.ico",
                baseName + ".ico"
            };
            foreach (string dir in dirs)
            {
                foreach (string name in names)
                {
                    string candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return exePath;
        }

        private static bool IsValidIco(byte[] ico)
        {
            if (ico == null || ico.Length < 6) return false;
            if (ico[0] != 0 || ico[1] != 0 || ico[2] != 1 || ico[3] != 0) return false;
            int count = BitConverter.ToUInt16(ico, 4);
            return count > 0 && 6 + count * 16 <= ico.Length;
        }

        private static byte[] ExtractIconBytes(string sourceExe)
        {
            if (string.IsNullOrEmpty(sourceExe) || !File.Exists(sourceExe)) return null;

            int[] sizes = { 256, 48, 32, 16 };
            List<int> widths = new List<int>();
            List<int> heights = new List<int>();
            List<byte[]> images = new List<byte[]>();

            foreach (int size in sizes)
            {
                IntPtr[] icons = new IntPtr[1];
                uint[] ids = new uint[1];
                try
                {
                    uint count = PrivateExtractIcons(sourceExe, 0, size, size, icons, ids, 1, 0);
                    if (count > 0 && icons[0] != IntPtr.Zero)
                    {
                        using (Bitmap bmp = Bitmap.FromHicon(icons[0]))
                        {
                            images.Add(EncodeIconImage(bmp, size >= 256));
                            widths.Add(bmp.Width);
                            heights.Add(bmp.Height);
                        }
                        DestroyIcon(icons[0]);
                    }
                }
                catch { if (icons[0] != IntPtr.Zero) DestroyIcon(icons[0]); }
            }

            if (images.Count == 0)
            {
                try
                {
                    using (Icon icon = Icon.ExtractAssociatedIcon(sourceExe))
                    {
                        if (icon != null)
                        {
                            using (Bitmap bmp = icon.ToBitmap())
                            {
                                images.Add(EncodeIconImage(bmp, false));
                                widths.Add(bmp.Width);
                                heights.Add(bmp.Height);
                            }
                        }
                    }
                }
                catch { }
            }

            if (images.Count == 0) return null;
            return BuildIcoBytes(widths, heights, images);
        }

        private static byte[] BuildIcoBytes(List<int> widths, List<int> heights, List<byte[]> images)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(ms))
            {
                int count = images.Count;
                writer.Write((ushort)0);
                writer.Write((ushort)1);
                writer.Write((ushort)count);
                int offset = 6 + 16 * count;
                for (int i = 0; i < count; i++)
                {
                    writer.Write((byte)(widths[i] >= 256 ? 0 : widths[i]));
                    writer.Write((byte)(heights[i] >= 256 ? 0 : heights[i]));
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((ushort)1);
                    writer.Write((ushort)32);
                    writer.Write((uint)images[i].Length);
                    writer.Write((uint)offset);
                    offset += images[i].Length;
                }
                foreach (byte[] image in images) writer.Write(image);
                writer.Flush();
                return ms.ToArray();
            }
        }

        private static bool PatchIcon(string exePath, byte[] ico)
        {
            if (ico == null || ico.Length < 6) return false;
            try
            {
                int count = BitConverter.ToUInt16(ico, 4);
                if (count <= 0) return false;

                IntPtr h = BeginUpdateResource(exePath, false);
                if (h == IntPtr.Zero) return false;

                bool ok = true;
                try
                {
                    List<byte[]> grpEntries = new List<byte[]>();
                    for (int i = 0; i < count; i++)
                    {
                        int e = 6 + i * 16;
                        if (e + 16 > ico.Length) { ok = false; break; }

                        byte w = ico[e];
                        byte hh = ico[e + 1];
                        byte cc = ico[e + 2];
                        byte res = ico[e + 3];
                        ushort planes = BitConverter.ToUInt16(ico, e + 4);
                        ushort bits = BitConverter.ToUInt16(ico, e + 6);
                        uint bytesInRes = BitConverter.ToUInt32(ico, e + 8);
                        uint imageOffset = BitConverter.ToUInt32(ico, e + 12);
                        if (imageOffset + bytesInRes > ico.Length) { ok = false; break; }

                        ushort id = (ushort)(i + 1);
                        byte[] image = new byte[bytesInRes];
                        Array.Copy(ico, (int)imageOffset, image, 0, (int)bytesInRes);
                        if (!UpdateResource(h, RT_ICON, (IntPtr)id, 0, image, (uint)image.Length)) { ok = false; break; }

                        using (MemoryStream m = new MemoryStream())
                        using (BinaryWriter bw = new BinaryWriter(m))
                        {
                            bw.Write(w);
                            bw.Write(hh);
                            bw.Write(cc);
                            bw.Write(res);
                            bw.Write(planes);
                            bw.Write(bits);
                            bw.Write(bytesInRes);
                            bw.Write(id);
                            bw.Flush();
                            grpEntries.Add(m.ToArray());
                        }
                    }

                    if (ok)
                    {
                        using (MemoryStream m = new MemoryStream())
                        using (BinaryWriter bw = new BinaryWriter(m))
                        {
                            bw.Write((ushort)0);
                            bw.Write((ushort)1);
                            bw.Write((ushort)count);
                            foreach (byte[] ge in grpEntries) bw.Write(ge);
                            bw.Flush();
                            byte[] grp = m.ToArray();
                            if (!UpdateResource(h, RT_GROUP_ICON, (IntPtr)1, 0, grp, (uint)grp.Length)) ok = false;
                        }
                    }
                }
                finally
                {
                    EndUpdateResource(h, !ok);
                }
                return ok;
            }
            catch { return false; }
        }

        private static bool BuildLauncherExe(string configText, string outDir, string exeName, bool requireAdmin, string iconSource, Action<string> log)
        {
            string tempDir = null;
            try
            {
                string sourceCode = LoadLauncherSourceText();
                string csc = FindCsc();
                if (string.IsNullOrEmpty(sourceCode) || csc == null)
                {
                    log("Launcher source or csc.exe not found.");
                    return false;
                }

                tempDir = Path.Combine(Path.GetTempPath(), "PortableBuilder_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);

                string sourcePath = Path.Combine(tempDir, "LauncherProgram.cs");
                File.WriteAllText(sourcePath, sourceCode, new UTF8Encoding(false));

                // Embed the launcher config as plain Base64 (no encryption).
                string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(configText));
                string code = "namespace PortableLauncher { internal static class EmbeddedConfig { " +
                    "public const string Base64 = \"" + b64 + "\"; " +
                    "public static string GetText() { return System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(Base64)); } } }";
                string configCs = Path.Combine(tempDir, "EmbeddedConfig.cs");
                File.WriteAllText(configCs, code, new UTF8Encoding(false));

                string manifest = Path.Combine(tempDir, "app.manifest");
                File.WriteAllText(manifest, ManifestText(requireAdmin), new UTF8Encoding(false));

                string iconPath = null;
                if (!string.IsNullOrEmpty(iconSource) && File.Exists(iconSource))
                {
                    try
                    {
                        string ico = Path.Combine(tempDir, "app.ico");
                        if (ExtractBestIcon(iconSource, ico)) iconPath = ico;
                    }
                    catch { iconPath = null; }
                }

                string outExe = Path.Combine(outDir, exeName);
                string appLabel = Path.GetFileNameWithoutExtension(exeName);
                string asmInfo = Path.Combine(tempDir, "AssemblyInfo.cs");
                File.WriteAllText(asmInfo, BuildAssemblyInfo(appLabel), new UTF8Encoding(false));

                string args = "/nologo /target:winexe /platform:anycpu /optimize+ /debug- " +
                    "/out:" + QuoteArg(outExe) +
                    " /win32manifest:" + QuoteArg(manifest) +
                    (iconPath != null ? " /win32icon:" + QuoteArg(iconPath) : "") +
                    " /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll " +
                    QuoteArg(sourcePath) + " " + QuoteArg(configCs) + " " + QuoteArg(asmInfo);

                ProcessStartInfo psi = new ProcessStartInfo(csc, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;

                using (Process process = Process.Start(psi))
                {
                    string stdout = process.StandardOutput.ReadToEnd();
                    string stderr = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    if (process.ExitCode != 0)
                    {
                        log("Compile failed: " + (stderr + " " + stdout).Trim());
                        return false;
                    }
                }

                if (!File.Exists(outExe)) return false;
                log("Per-app launcher compiled: " + outExe);
                return true;
            }
            catch (Exception ex)
            {
                log("Compile error: " + ex.Message);
                return false;
            }
            finally
            {
                if (tempDir != null)
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        private static string ManifestText(bool requireAdmin)
        {
            string level = requireAdmin ? "requireAdministrator" : "asInvoker";
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
                "<assembly manifestVersion=\"1.0\" xmlns=\"urn:schemas-microsoft-com:asm.v1\">\r\n" +
                "  <assemblyIdentity version=\"1.1.0.0\" name=\"PortableLauncher.app\" />\r\n" +
                "  <trustInfo xmlns=\"urn:schemas-microsoft-com:asm.v2\">\r\n" +
                "    <security>\r\n" +
                "      <requestedPrivileges xmlns=\"urn:schemas-microsoft-com:asm.v3\">\r\n" +
                "        <requestedExecutionLevel level=\"" + level + "\" uiAccess=\"false\" />\r\n" +
                "      </requestedPrivileges>\r\n" +
                "    </security>\r\n" +
                "  </trustInfo>\r\n" +
                "  <compatibility xmlns=\"urn:schemas-microsoft-com:compatibility.v1\">\r\n" +
                "    <application>\r\n" +
                "      <supportedOS Id=\"{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}\" />\r\n" +
                "    </application>\r\n" +
                "  </compatibility>\r\n" +
                "</assembly>\r\n";
        }

        private static string LoadLauncherSourceText()
        {
            string external = FindLauncherSource();
            if (external != null)
            {
                try { return File.ReadAllText(external); }
                catch { }
            }

            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream stream = asm.GetManifestResourceStream("PortableBuilder.LauncherProgram.cs"))
                {
                    if (stream != null)
                    {
                        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                            return reader.ReadToEnd();
                    }
                }
            }
            catch { }
            return null;
        }

        private static string FindLauncherSource()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string[] candidates =
            {
                Path.Combine(dir, "..", "PortableLauncher", "Program.cs"),
                Path.Combine(dir, "PortableLauncher", "Program.cs"),
                Path.Combine(dir, "src", "PortableLauncher", "Program.cs"),
                Path.Combine(dir, "..", "..", "PortableLauncher", "Program.cs")
            };
            foreach (string candidate in candidates)
            {
                try
                {
                    string full = Path.GetFullPath(candidate);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
            return null;
        }

        private static string FindCsc()
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string[] candidates =
            {
                Path.Combine(win, "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe"),
                Path.Combine(win, "Microsoft.NET", "Framework", "v4.0.30319", "csc.exe")
            };
            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static string QuoteArg(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static void NormalizeRequest(BuildRequest request)
        {
            if (request == null) return;
            if (request.DataPaths == null) request.DataPaths = new List<string>();
            if (request.RegistryKeys == null) request.RegistryKeys = new List<string>();
            if (request.Services == null) request.Services = new List<string>();
            if (request.ExcludePaths == null) request.ExcludePaths = new List<string>();
            if (request.RegistryReplace == null) request.RegistryReplace = new List<string[]>();
            if (request.Args == null) request.Args = "";
            if (request.AppName == null) request.AppName = "";
            if (request.AppId == null) request.AppId = "";
            if (request.InstallTarget == null) request.InstallTarget = "";
            if (request.WaitProcess == null) request.WaitProcess = "";
        }

        public static BuildResult Build(BuildRequest request, Action<string> log)
        {
            NormalizeRequest(request);
            if (request != null && request.BestLayout) return BuildBestLayout(request, log);

            BuildResult result = new BuildResult();
            try
            {
                if (request == null) throw new ArgumentNullException("request");
                if (log == null) log = delegate(string s) { };

                string appName = PathUtil.SafeName(request.AppName);
                if (appName.Length == 0) throw new InvalidOperationException("App name is required.");

                string installPath = request.InstallPath;
                if (string.IsNullOrEmpty(installPath)) throw new InvalidOperationException("Install path is required.");
                installPath = Path.GetFullPath(installPath).TrimEnd('\\');
                if (File.Exists(installPath)) installPath = Path.GetDirectoryName(installPath);
                if (!Directory.Exists(installPath)) throw new DirectoryNotFoundException("Install folder not found: " + installPath);

                if (request.EnableRobloxSetup &&
                    string.Equals(request.AppId, "roblox", StringComparison.OrdinalIgnoreCase))
                    return BuildRoblox(request, installPath, request.ExePath, log);
                if (request.EnableGameLoopSetup &&
                    string.Equals(request.AppId, "gameloop", StringComparison.OrdinalIgnoreCase))
                    return BuildGameLoop(request, installPath, request.ExePath, log);
                if (request.EnableFiveMSetup &&
                    string.Equals(request.AppId, "fivem", StringComparison.OrdinalIgnoreCase))
                    return BuildFiveM(request, installPath, request.ExePath, log);

                string exePath = request.ExePath;
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                    throw new FileNotFoundException("Main executable not found: " + exePath);

                string installTarget = request.InstallTarget;
                if (request.CleanPaths && string.IsNullOrEmpty(installTarget) && IsCustomPath(installPath))
                {
                    installTarget = PickCleanTarget(appName, installPath);
                    log("Clean path remap: " + installPath + " -> " + installTarget);
                }

                string linkPath;
                string runExe;
                ResolveInstallTarget(installTarget, installPath, exePath, out linkPath, out runExe);
                List<string[]> registryReplacements = BuildRegistryReplacements(request, installPath, linkPath);

                WarnAbsolutePath(linkPath, log);
                foreach (string rawData in request.DataPaths) WarnAbsolutePath(PathUtil.Expand(rawData), log);

                string outDir = Path.GetFullPath(request.OutputDir);
                if (PathUtil.IsUnsafeOutput(outDir))
                    throw new InvalidOperationException("Refusing to use an unsafe output folder: " + outDir);
                if (request.BestJunction && outDir.StartsWith(@"\\", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "Best + Junction needs a local NTFS output folder, not a network share.\r\n" +
                        "Use Best Portable layout (Symlink) for UNC/share outputs.");
                if (PathUtil.IsSameOrSubPath(outDir, installPath) || PathUtil.IsSameOrSubPath(installPath, outDir))
                    throw new InvalidOperationException("Output folder must not overlap the install folder.");
                foreach (string rawData in request.DataPaths)
                {
                    string data = PathUtil.Expand(rawData);
                    if (string.IsNullOrEmpty(data)) continue;
                    try { data = Path.GetFullPath(data); }
                    catch { continue; }
                    if (PathUtil.IsSameOrSubPath(outDir, data) || PathUtil.IsSameOrSubPath(data, outDir))
                        throw new InvalidOperationException("Output folder must not overlap a data folder: " + data);
                }

                PreparePackageDirectory(outDir, appName);

                string dataRoot = Path.Combine(outDir, "Data");
                string regsRoot = Path.Combine(outDir, "Regs");
                Directory.CreateDirectory(dataRoot);

                List<string> links = new List<string>();
                List<string> copies = new List<string>();
                int copied = 0;

                // ---- install folder ----
                string folderName = PathUtil.SafeName(Path.GetFileName(installPath.TrimEnd('\\')));
                if (folderName.Length == 0) folderName = appName;

                string installRel = Path.Combine(request.CopyMode ? "Copy" : "Links", "Install", folderName);
                string installAbs = Path.Combine(dataRoot, installRel);
                log((request.CopyMode ? "Copying" : "Copying for folder redirection") + ": " + installPath);
                int installFailures;
                copied += PathUtil.CopyTree(installPath, installAbs, log, request.ExcludePaths, out installFailures);
                if (installFailures > 0)
                    throw new InvalidOperationException("Install folder copy failed for " + installFailures +
                        " item(s); package was not completed.");

                if (request.CopyMode) copies.Add(PathUtil.ToEnv(linkPath) + "=" + Path.Combine("Data", installRel));
                else links.Add(PathUtil.ToEnv(linkPath) + "=" + Path.Combine("Data", installRel));

                // ---- data folders ----
                List<string> dataPaths = PathUtil.NormalizePathList(request.DataPaths);
                dataPaths.Sort(delegate(string a, string b) { return a.Length.CompareTo(b.Length); });
                List<string> acceptedData = new List<string>();
                int dataIndex = 0;

                foreach (string dataPath in dataPaths)
                {
                    string full = Path.GetFullPath(dataPath).TrimEnd('\\');
                    if (PathUtil.IsDriveRoot(full)) continue;
                    if (PathUtil.IsSubPathOf(full, installPath) || PathUtil.IsSubPathOf(installPath, full)) continue;

                    bool covered = false;
                    foreach (string accepted in acceptedData)
                    {
                        if (PathUtil.IsSubPathOf(full, accepted)) { covered = true; break; }
                    }
                    if (covered) continue;

                    if (File.Exists(full))
                    {
                        string parent = Path.GetDirectoryName(full);
                        if (string.IsNullOrEmpty(parent) || PathUtil.IsDriveRoot(parent) ||
                            PathUtil.IsUnsafeOutput(parent))
                        {
                            log("Data file skipped (unsafe parent folder): " + full);
                            continue;
                        }
                        string rel = Path.Combine("Overlay", "Data", dataIndex.ToString());
                        string abs = Path.Combine(dataRoot, rel);
                        Directory.CreateDirectory(abs);
                        PathUtil.CopyOneFile(full, Path.Combine(abs, Path.GetFileName(full)), log);
                        copied++;
                        links.Add(PathUtil.ToEnv(parent) + "=" + Path.Combine("Data", rel));
                        acceptedData.Add(full);
                        dataIndex++;
                        continue;
                    }

                    if (!Directory.Exists(full)) continue;

                    string name = PathUtil.SafeName(Path.GetFileName(full.TrimEnd('\\')));
                    if (name.Length == 0) name = "Data" + dataIndex;
                    string relDir = Path.Combine(request.CopyMode ? "Copy" : "Links", "Data",
                        dataIndex.ToString(), name);
                    string absDir = Path.Combine(dataRoot, relDir);
                    log("Data folder: " + full);
                    int dataFailures;
                    copied += PathUtil.CopyTree(full, absDir, log, null, out dataFailures);
                    if (dataFailures > 0)
                        log("Warning: " + dataFailures + " file(s) failed to copy from " + full);

                    if (request.CopyMode) copies.Add(PathUtil.ToEnv(full) + "=" + Path.Combine("Data", relDir));
                    else links.Add(PathUtil.ToEnv(full) + "=" + Path.Combine("Data", relDir));

                    acceptedData.Add(full);
                    dataIndex++;
                }

                // ---- registry ----
                List<string> regEntries = new List<string>();
                List<string> exportEntries = new List<string>();
                int regIndex = 0;
                bool hasMachineKey = false;

                if (request.IncludeRegistry)
                {
                    List<string> keys = new List<string>();
                    HashSet<string> seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string rawKey in request.RegistryKeys)
                    {
                        if (string.IsNullOrEmpty(rawKey)) continue;
                        string normalized = PathUtil.NormalizeRegistryKey(PathUtil.Expand(rawKey));
                        if (normalized.Length == 0) continue;
                        if (seenKeys.Add(normalized)) keys.Add(normalized);
                    }
                    keys.Sort(delegate(string a, string b) { return a.Length.CompareTo(b.Length); });
                    List<string> acceptedKeys = new List<string>();

                    foreach (string key in keys)
                    {
                        string fullKey = PathUtil.NormalizeRegistryKey(key);
                        if (fullKey.Length == 0) continue;
                        if (PathUtil.SkipRegistryKey(fullKey)) continue;
                        if (!RegistryUtil.KeyExists(fullKey))
                        {
                            log("Registry key not found, skipped: " + fullKey);
                            continue;
                        }

                        bool covered = false;
                        foreach (string accepted in acceptedKeys)
                        {
                            if (fullKey.StartsWith(accepted + "\\", StringComparison.OrdinalIgnoreCase))
                            {
                                covered = true;
                                break;
                            }
                        }
                        if (covered) continue;

                        if (fullKey.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase) ||
                            fullKey.StartsWith("HKEY_CLASSES_ROOT", StringComparison.OrdinalIgnoreCase))
                            hasMachineKey = true;

                        Directory.CreateDirectory(regsRoot);
                        string file = Path.Combine(regsRoot, "reg_" + regIndex + ".reg");
                        log("Exporting registry: " + fullKey);
                        if (RegistryUtil.ExportKey(fullKey, file, log))
                        {
                            RegistryUtil.ApplyReplacements(file, registryReplacements, log);
                            regEntries.Add("reg" + regIndex + "=" + Path.Combine("Regs", "reg_" + regIndex + ".reg"));

                            if (fullKey.IndexOf("\\Uninstall\\", StringComparison.OrdinalIgnoreCase) < 0 &&
                                fullKey.IndexOf("\\Services\\", StringComparison.OrdinalIgnoreCase) < 0 &&
                                exportEntries.Count < 10)
                            {
                                exportEntries.Add(fullKey + "=" + Path.Combine("Regs", "export_" + exportEntries.Count + ".reg"));
                            }
                            acceptedKeys.Add(fullKey);
                            regIndex++;
                        }
                    }
                }

                // ---- services ----
                List<string> serviceEntries = new List<string>();
                HashSet<string> seenServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (string serviceName in request.Services)
                {
                    string name = serviceName.Trim();
                    if (name.Length == 0 || !seenServices.Add(name)) continue;

                    string keyPath = "HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Services\\" + name;
                    string imagePath = RegistryUtil.ReadString(keyPath, "ImagePath");
                    object typeValue = RegistryUtil.ReadValue(keyPath, "Type");
                    int type = typeValue is int ? (int)typeValue : 0;

                    if (string.IsNullOrEmpty(imagePath)) continue;
                    const int SvcKernel = 0x1, SvcFs = 0x2, SvcWin32Own = 0x10, SvcWin32Share = 0x20;
                    bool kernel = (type & (SvcKernel | SvcFs)) != 0;
                    bool userMode = (type & (SvcWin32Own | SvcWin32Share)) != 0;
                    if (kernel || !userMode)
                    {
                        log("Non-user-mode service skipped: " + name);
                        continue;
                    }

                    string display = RegistryUtil.ReadString(keyPath, "DisplayName");
                    string description = RegistryUtil.ReadString(keyPath, "Description");
                    if (string.IsNullOrEmpty(display)) display = name;
                    if (description == null) description = "";

                    string portableImagePath = RemapServiceImagePath(imagePath, installPath, linkPath);

                    serviceEntries.Add(SanitizeServicePart(name) + "=" + SanitizeServicePart(portableImagePath) + "|" +
                        SanitizeServicePart(display) + "|" +
                        SanitizeServicePart(description));
                    hasMachineKey = true;
                }

                // ---- admin ----
                bool needsAdmin = hasMachineKey || serviceEntries.Count > 0;
                if (PathUtil.IsAdminPath(installPath) || PathUtil.IsAdminPath(linkPath)) needsAdmin = true;
                foreach (string data in acceptedData)
                {
                    if (PathUtil.IsAdminPath(data)) needsAdmin = true;
                }

                bool useSymlink = request.ForceSymlink || needsAdmin ||
                                  (!request.CopyMode && outDir.StartsWith(@"\\", StringComparison.Ordinal));
                bool requireAdmin = needsAdmin || request.ForceSymlink ||
                                    (!request.CopyMode && outDir.StartsWith(@"\\", StringComparison.Ordinal));
                if (log != null) log("Link type: " + (useSymlink ? "Symlink" : "Junction"));

                // ---- launcher.ini ----
                StringBuilder ini = new StringBuilder();
                ini.AppendLine("; Generated by PortableBuilder GUI on " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                ini.AppendLine("; App: " + appName);
                ini.AppendLine();
                ini.AppendLine("[App]");
                ini.AppendLine("Name=" + appName);
                ini.AppendLine("LinkType=" + (useSymlink ? "Symlink" : "Junction"));
                ini.AppendLine("RequireAdmin=" + (requireAdmin ? "1" : "0"));
                ini.AppendLine("Silent=1");
                ini.AppendLine("LogFile=");
                ini.AppendLine("WaitForExit=" + (request.WaitForExit ? "1" : "0"));
                ini.AppendLine("RestoreOnExit=" + (request.RestoreOnExit ? "1" : "0"));
                ini.AppendLine("DelayMs=0");
                if (!string.IsNullOrEmpty(request.WaitProcess))
                    ini.AppendLine("WaitProcess=" + request.WaitProcess.Trim());
                ini.AppendLine();

                ini.AppendLine("[Folders]");
                foreach (string line in links) ini.AppendLine(line);
                ini.AppendLine();

                ini.AppendLine("[CopyFolders]");
                foreach (string line in copies) ini.AppendLine(line);
                ini.AppendLine();

                ini.AppendLine("[Overlay]");
                ini.AppendLine();

                ini.AppendLine("[Registry]");
                foreach (string line in regEntries) ini.AppendLine(line);
                for (int i = 0; i < exportEntries.Count; i++)
                    ini.AppendLine("export" + i + "=" + Path.Combine("Regs", "export_" + i + ".reg"));
                ini.AppendLine();

                ini.AppendLine("[RegistryExport]");
                foreach (string line in exportEntries) ini.AppendLine(line);
                ini.AppendLine();

                ini.AppendLine("[Services]");
                foreach (string line in serviceEntries) ini.AppendLine(line);
                ini.AppendLine();

                ini.AppendLine("[Run]");
                ini.AppendLine(PathUtil.ToEnv(runExe) + "=" + (request.Args == null ? "" : request.Args));

                string configText = ini.ToString();
                string launcherName = appName + ".exe";
                string iconSource = FindIconSource(exePath, installPath);
                bool compiled = BuildLauncherFromTemplate(configText, outDir, launcherName, iconSource, log);

                if (!compiled)
                    throw new InvalidOperationException("Launcher creation failed; package was not completed.");
                result.LauncherPath = Path.Combine(outDir, launcherName);

                // Generate an uninstaller next to the launcher.
                string uninstallName = "Uninstall_" + appName + ".exe";
                if (BuildLauncherFromTemplate(MakeUninstallConfig(configText), outDir, uninstallName, iconSource, log))
                    log("Uninstaller created: " + uninstallName);

                ValidatePackage(outDir, appName);

                // ---- report ----
                StringBuilder report = new StringBuilder();
                report.AppendLine("PortableBuilder GUI report");
                report.AppendLine("App: " + appName);
                report.AppendLine("Date: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                report.AppendLine("Install: " + installPath);
                report.AppendLine("Exe: " + exePath);
                report.AppendLine("Mode: " + (request.CopyMode ? "Copy" : (useSymlink ? "Symlink" : "Junction")));
                report.AppendLine("RequireAdmin: " + requireAdmin);
                report.AppendLine();
                report.AppendLine("Links (" + links.Count + ")");
                foreach (string line in links) report.AppendLine(line);
                report.AppendLine();
                report.AppendLine("Copy folders (" + copies.Count + ")");
                foreach (string line in copies) report.AppendLine(line);
                report.AppendLine();
                report.AppendLine("Registry (" + regEntries.Count + ")");
                foreach (string line in regEntries) report.AppendLine(line);
                report.AppendLine();
                report.AppendLine("Services (" + serviceEntries.Count + ")");
                foreach (string line in serviceEntries) report.AppendLine(line);
                report.AppendLine();
                report.AppendLine("Files copied: " + copied);
                report.AppendLine("Notes: review launcher.ini and changes before distribution.");
                // (changes.txt report intentionally not written: silent builds only)

                result.Success = true;
                result.OutputDir = outDir;
                result.FilesCopied = copied;
                result.RegistryFiles = regEntries.Count;
                result.Services = serviceEntries.Count;
                log("DONE: " + outDir);
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Error = ex.Message;
                log("ERROR: " + ex);
                return result;
            }
        }
    }

    // =====================================================================
    //  Helpers
    // =====================================================================

    public static class RegistryUtil
    {
        public static RegistryKey OpenKey(string fullPath, bool writable)
        {
            if (string.IsNullOrEmpty(fullPath)) return null;
            fullPath = fullPath.Trim();

            int split = fullPath.IndexOf('\\');
            string hiveName = split < 0 ? fullPath : fullPath.Substring(0, split);
            string subKey = split < 0 ? "" : fullPath.Substring(split + 1);

            RegistryHive hive;
            if (string.Equals(hiveName, "HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(hiveName, "HKLM", StringComparison.OrdinalIgnoreCase)) hive = RegistryHive.LocalMachine;
            else if (string.Equals(hiveName, "HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(hiveName, "HKCU", StringComparison.OrdinalIgnoreCase)) hive = RegistryHive.CurrentUser;
            else if (string.Equals(hiveName, "HKEY_CLASSES_ROOT", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(hiveName, "HKCR", StringComparison.OrdinalIgnoreCase)) hive = RegistryHive.ClassesRoot;
            else if (string.Equals(hiveName, "HKEY_USERS", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(hiveName, "HKU", StringComparison.OrdinalIgnoreCase)) hive = RegistryHive.Users;
            else return null;

            RegistryView[] views = Environment.Is64BitOperatingSystem
                ? new[] { RegistryView.Registry64, RegistryView.Registry32 }
                : new[] { RegistryView.Default };

            foreach (RegistryView view in views)
            {
                RegistryKey baseKey = null;
                try
                {
                    baseKey = RegistryKey.OpenBaseKey(hive, view);
                    if (subKey.Length == 0) return baseKey;
                    RegistryKey result = baseKey.OpenSubKey(subKey, writable);
                    baseKey.Dispose();
                    if (result != null) return result;
                }
                catch { if (baseKey != null) baseKey.Dispose(); }
            }
            return null;
        }

        public static bool KeyExists(string fullPath)
        {
            try
            {
                using (RegistryKey key = OpenKey(fullPath, false))
                    return key != null;
            }
            catch { return false; }
        }

        // Flags registry keys that commonly hold account identity, credentials,
        // sessions, licenses or user history, so the UI can warn the user before
        // they package/share that data. Notes are deliberately short and Arabic.
        public static string SensitiveRegistryNote(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            string k = key.Replace('/', '\\').Trim();
            string lower = k.ToLowerInvariant();
            bool appScoped =
                (lower.StartsWith("hkey_current_user\\software\\", StringComparison.Ordinal) ||
                 lower.StartsWith("hkey_local_machine\\software\\", StringComparison.Ordinal) ||
                 lower.StartsWith("hkcu\\software\\", StringComparison.Ordinal) ||
                 lower.StartsWith("hklm\\software\\", StringComparison.Ordinal)) &&
                lower.IndexOf("\\microsoft\\windows\\currentversion\\uninstall", StringComparison.Ordinal) < 0;

            string[] strongSegments =
            {
                "credentials", "storedidentities", "identitycrl", "identities",
                "token", "tokens", "auth", "authentication", "login", "signin",
                "accounts", "password", "passwords", "savedpasswords", "autofill",
                "cookies", "sessions", "connectcache", "autologinuser",
                "rememberpassword", "license", "licenses", "serial", "productkey",
                "cdkey", "activation", "registration", "wallet", "billing",
                "payment", "messages", "chathistory", "contacts", "friends",
                "savedgames", "savegames", "sshhostkeys"
            };
            foreach (string marker in strongSegments)
            {
                if (HasRegistrySegment(lower, marker))
                    return "قد يحتوي على بيانات حساب أو بريد إلكتروني أو جلسة/ترخيص. راجع القيمة قبل مشاركة الباكدج.";
            }

            if (appScoped)
            {
                string[] userData = { "users", "profiles", "userdata", "saves", "history", "bookmarks" };
                foreach (string marker in userData)
                {
                    if (HasRegistrySegment(lower, marker))
                        return "قد يحتوي على ملفات/إعدادات مستخدم أو سجل استخدام خاص بهذا الجهاز.";
                }

                string[] launchers =
                {
                    "\\valve\\steam", "\\epic games", "\\blizzard entertainment",
                    "\\riot games", "\\rockstar games", "\\ubisoft",
                    "\\electronic arts", "\\gog.com", "\\discord",
                    "\\telegramdesktop", "\\battle.net"
                };
                foreach (string marker in launchers)
                {
                    if (lower.IndexOf(marker, StringComparison.Ordinal) >= 0)
                        return "قد يحتوي على اسم مستخدم/معرّف حساب أو بيانات تسجيل دخول محفوظة.";
                }
            }
            return null;
        }

        private static bool HasRegistrySegment(string lowerKey, string segment)
        {
            if (string.IsNullOrEmpty(lowerKey) || string.IsNullOrEmpty(segment)) return false;
            string needle = "\\" + segment.ToLowerInvariant();
            int index = lowerKey.IndexOf(needle, StringComparison.Ordinal);
            while (index >= 0)
            {
                int end = index + needle.Length;
                if (end == lowerKey.Length || lowerKey[end] == '\\') return true;
                index = lowerKey.IndexOf(needle, index + 1, StringComparison.Ordinal);
            }
            return false;
        }

        public static string ReadString(string fullPath, string valueName)
        {
            object value = ReadValue(fullPath, valueName);
            return value is string ? (string)value : null;
        }

        public static object ReadValue(string fullPath, string valueName)
        {
            try
            {
                using (RegistryKey key = OpenKey(fullPath, false))
                {
                    if (key == null) return null;
                    return key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                }
            }
            catch { return null; }
        }

        public static string ReadString(RegistryKey key, string valueName)
        {
            try
            {
                object value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                return value is string ? (string)value : null;
            }
            catch { return null; }
        }

        public static bool ExportKey(string fullPath, string file, Action<string> log)
        {
            if (string.IsNullOrEmpty(fullPath) || string.IsNullOrEmpty(file)) return false;
            try
            {
                string parent = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                if (File.Exists(file)) { try { File.Delete(file); } catch { } }

                string reg = Path.Combine(Environment.SystemDirectory, "reg.exe");
                string stdout;
                string stderr;
                int code = RunReg(reg, "export " + Quote(fullPath) + " " + Quote(file) + " /y",
                    out stdout, out stderr);

                // OpenKey probes both registry views. reg.exe defaults to the
                // 64-bit view, so retry the 32-bit view when the key only exists
                // there (common for old HKCU\Software apps).
                if (code != 0 && Environment.Is64BitOperatingSystem)
                {
                    try { if (File.Exists(file)) File.Delete(file); } catch { }
                    int code32 = RunReg(reg,
                        "export " + Quote(fullPath) + " " + Quote(file) + " /y /reg:32",
                        out stdout, out stderr);
                    if (code32 == 0) code = 0;
                }

                if (code != 0)
                {
                    if (log != null) log("reg export failed (" + code + "): " + (stderr + " " + stdout).Trim());
                    try { if (File.Exists(file)) File.Delete(file); } catch { }
                    return false;
                }
                try { return File.Exists(file) && new FileInfo(file).Length > 0; }
                catch { return false; }
            }
            catch (Exception ex)
            {
                if (log != null) log("reg export error: " + ex.Message);
                return false;
            }
        }

        private static int RunReg(string exe, string args, out string stdout, out string stderr)
        {
            stdout = "";
            stderr = "";
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;

            using (Process p = Process.Start(psi))
            {
                if (p == null) return -1;
                System.Threading.Tasks.Task<string> outTask = p.StandardOutput.ReadToEndAsync();
                System.Threading.Tasks.Task<string> errTask = p.StandardError.ReadToEndAsync();

                bool exited = p.WaitForExit(120000);
                if (!exited)
                {
                    try { p.Kill(); } catch { }
                    try { p.WaitForExit(5000); } catch { }
                }
                try { if (outTask.Wait(5000)) stdout = outTask.Result; } catch { }
                try { if (errTask.Wait(5000)) stderr = errTask.Result; } catch { }
                if (!exited)
                {
                    stderr = (stderr + " reg.exe timed out.").Trim();
                    return -1;
                }
                return p.ExitCode;
            }
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        public static void ApplyReplacements(string file, List<string[]> pairs, Action<string> log)
        {
            if (pairs == null || pairs.Count == 0 || !File.Exists(file)) return;

            try
            {
                string text = File.ReadAllText(file, Encoding.Unicode);
                int changed = 0;

                foreach (string[] pair in pairs)
                {
                    if (pair == null || pair.Length < 2) continue;
                    string oldValue = pair[0] ?? "";
                    string newValue = pair[1] ?? "";
                    if (oldValue.Length == 0) continue;

                    string oldEscaped = oldValue.Replace("\\", "\\\\");
                    string newEscaped = newValue.Replace("\\", "\\\\");
                    string before = text;

                    // Line-by-line so binary hex(...) values are never altered.
                    string[] lines = text.Split('\n');
                    StringBuilder sb = new StringBuilder(text.Length);
                    for (int li = 0; li < lines.Length; li++)
                    {
                        string line = lines[li];
                        if (line.IndexOf("hex:", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            sb.Append(line);
                        }
                        else
                        {
                            string l = ReplaceIgnoreCase(line, oldValue, newValue);
                            if (!string.Equals(oldEscaped, oldValue, StringComparison.Ordinal))
                                l = ReplaceIgnoreCase(l, oldEscaped, newEscaped);
                            sb.Append(l);
                        }
                        if (li < lines.Length - 1) sb.Append('\n');
                    }
                    text = sb.ToString();

                    if (!string.Equals(before, text, StringComparison.Ordinal)) changed++;
                }

                if (changed > 0)
                {
                    File.WriteAllText(file, text, Encoding.Unicode);
                    if (log != null) log("Registry paths rewritten: " + changed + " rule(s) in " + Path.GetFileName(file));
                }
            }
            catch (Exception ex)
            {
                if (log != null) log("Registry rewrite failed: " + Path.GetFileName(file) + " : " + ex.Message);
            }
        }

        private static string ReplaceIgnoreCase(string text, string oldValue, string newValue)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(oldValue)) return text;

            StringBuilder result = new StringBuilder(text.Length);
            int index = 0;
            while (true)
            {
                int found = text.IndexOf(oldValue, index, StringComparison.OrdinalIgnoreCase);
                if (found < 0)
                {
                    result.Append(text, index, text.Length - index);
                    break;
                }
                result.Append(text, index, found - index);
                result.Append(newValue);
                index = found + oldValue.Length;
            }
            return result.ToString();
        }
    }

    public static class PathUtil
    {
        public static readonly HashSet<string> CommonTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "app", "apps", "application", "client", "launcher", "desktop", "studio",
            "software", "manager", "service", "update", "updater", "helper", "runtime",
            "portable", "games", "game", "tool", "tools", "suite", "plus", "player",
            "media", "server", "agent", "control", "center", "home", "free", "pro",
            "premium", "x86", "x64", "win", "windows", "microsoft", "google", "the",
            "official", "edition", "version", "browser", "play", "code", "music",
            "photo", "image", "video", "chat", "search", "archive", "download",
            "remote", "security", "cleaner", "driver", "antivirus", "messaging",
            "editor", "viewer", "reader", "writer", "notes", "sync", "backup",
            "recovery", "network", "monitor", "system", "bootstrap", "support",
            "oracle", "java", "jdk"
        };

        public static readonly HashSet<string> SharedFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "microsoft", "windows", "windowsapps", "packages", "temp", "tmp", "cache",
            "caches", "crashdumps", "d3dscache", "programdata", "public", "users",
            "commonfiles", "system32", "syswow64", "winsxs", "installer"
        };

        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            StringBuilder sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        public static List<string> WordTokens(string text)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;

            StringBuilder current = new StringBuilder();
            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c))
                {
                    current.Append(char.ToLowerInvariant(c));
                }
                else if (current.Length > 0)
                {
                    if (current.Length >= 3) result.Add(current.ToString());
                    current.Length = 0;
                }
            }
            if (current.Length >= 3) result.Add(current.ToString());
            return result;
        }

        public static string Expand(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        }

        public static string CleanPath(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string s = value.Trim();
            if (s.StartsWith("\"") && s.Length > 1)
            {
                int end = s.IndexOf('"', 1);
                if (end > 1) s = s.Substring(1, end - 1);
            }
            int exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe > 0) s = s.Substring(0, exe + 4);
            return s.Trim();
        }

        public static string CommandPath(string command)
        {
            if (string.IsNullOrEmpty(command)) return "";
            string s = command.Trim();

            if (s.StartsWith("\""))
            {
                int end = s.IndexOf('"', 1);
                if (end > 1) return s.Substring(1, end - 1).Trim();
            }

            int comma = s.IndexOf(',');
            if (comma > 0)
            {
                string tail = s.Substring(comma + 1).Trim();
                bool allDigits = tail.Length > 0;
                foreach (char c in tail)
                {
                    if (!char.IsDigit(c)) { allDigits = false; break; }
                }
                if (allDigits) s = s.Substring(0, comma).Trim();
            }

            int exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe > 0) s = s.Substring(0, exe + 4);
            return s.Trim();
        }

        public static string SafeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "MyApp";
            StringBuilder sb = new StringBuilder(name.Length);
            char[] invalid = Path.GetInvalidFileNameChars();
            foreach (char c in name)
            {
                if (Array.IndexOf(invalid, c) >= 0) sb.Append('_');
                else sb.Append(c);
            }
            string result = sb.ToString().Trim().Trim('.');
            if (result.Length == 0) return "MyApp";

            // Windows reserves these device names for any extension.
            string upper = result.ToUpperInvariant();
            string stem = upper;
            int dot = stem.IndexOf('.');
            if (dot >= 0) stem = stem.Substring(0, dot);
            if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                                      stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                 stem[3] >= '1' && stem[3] <= '9'))
            {
                result = "_" + result;
            }
            return result;
        }

        public static bool IsDriveRoot(string path)
        {
            string full = Path.GetFullPath(path).TrimEnd('\\');
            return full.Length == 2 && full[1] == ':';
        }

        // Refuse outputs that would overwrite a user/system folder or a parent
        // of the current working directory.
        public static bool IsUnsafeOutput(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            string full;
            try { full = Path.GetFullPath(path).TrimEnd('\\'); }
            catch { return true; }

            if (full.Length == 0) return true;
            if (full.StartsWith(@"\\?\") || full.StartsWith(@"\\.\")) return true;
            if (IsDriveRoot(full)) return true;

            string cwd;
            try { cwd = Path.GetFullPath(Directory.GetCurrentDirectory()).TrimEnd('\\'); }
            catch { cwd = ""; }
            if (cwd.Length > 0 && IsSameOrSubPath(cwd, full)) return true;

            string userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string usersRootPath = "";
            try
            {
                if (!string.IsNullOrEmpty(userProfilePath))
                    usersRootPath = Path.GetDirectoryName(userProfilePath) ?? "";
            }
            catch { }

            string[] reserved =
            {
                userProfilePath,
                usersRootPath,
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.System)
            };
            foreach (string candidate in reserved)
            {
                if (string.IsNullOrEmpty(candidate)) continue;
                try
                {
                    if (string.Equals(Path.GetFullPath(candidate).TrimEnd('\\'), full,
                            StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch { }
            }

            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(windows))
            {
                try
                {
                    windows = Path.GetFullPath(windows).TrimEnd('\\');
                    if (string.Equals(full, windows, StringComparison.OrdinalIgnoreCase) ||
                        full.StartsWith(windows + "\\", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { }
            }
            return false;
        }

        public static bool IsBadInstallPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            string p = path.Replace('/', '\\').ToLowerInvariant();
            if (p.StartsWith("c:\\windows\\")) return true;
            if (p.Contains("\\windows\\installer")) return true;
            if (p.Contains("\\windowsapps\\")) return true;
            if (p.Contains("\\package cache\\")) return true;
            if (p.Contains("\\edgewebview\\")) return true;
            if (p.Contains("\\temp\\")) return true;
            if (p.EndsWith("\\temp")) return true;
            if (p.EndsWith("\\installer")) return true;
            if (p.Contains("\\microsoft visual studio\\installer")) return true;
            if (p.Contains("\\windows\\system32")) return true;
            return false;
        }

        public static bool IsBadUninstallDisplayName(string normalizedName)
        {
            if (string.IsNullOrEmpty(normalizedName)) return true;
            string n = normalizedName.ToLowerInvariant();
            return n.Contains("webview") || n.Contains("redistributable") ||
                   n.Contains("installer") || n.Contains("updater") ||
                   n.Contains("update") || n.Contains("bootstrapper") ||
                   n.Contains("setup");
        }

        public static bool IsAdminPath(string path)
        {
            string env = ToEnv(path);
            return env.StartsWith("%ProgramFiles%", StringComparison.OrdinalIgnoreCase)
                || env.StartsWith("%ProgramFiles(x86)%", StringComparison.OrdinalIgnoreCase)
                || env.StartsWith("%ProgramData%", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsSubPathOf(string child, string parent)
        {
            if (string.IsNullOrEmpty(child) || string.IsNullOrEmpty(parent)) return false;
            try
            {
                child = Path.GetFullPath(child).TrimEnd('\\');
                parent = Path.GetFullPath(parent).TrimEnd('\\');
                return child.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static bool IsSameOrSubPath(string child, string parent)
        {
            if (string.IsNullOrEmpty(child) || string.IsNullOrEmpty(parent)) return false;
            try
            {
                child = Path.GetFullPath(child).TrimEnd('\\');
                parent = Path.GetFullPath(parent).TrimEnd('\\');
                return string.Equals(child, parent, StringComparison.OrdinalIgnoreCase)
                    || child.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static string ToEnv(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return fullPath;
            string path = Path.GetFullPath(fullPath);

            string localLow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow");
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

            string replaced = ReplacePrefix(path, localLow, "%USERPROFILE%\\AppData\\LocalLow");
            if (replaced != null) return replaced;
            replaced = ReplacePrefix(path, local, "%LOCALAPPDATA%");
            if (replaced != null) return replaced;
            replaced = ReplacePrefix(path, roaming, "%APPDATA%");
            if (replaced != null) return replaced;
            replaced = ReplacePrefix(path, common, "%ProgramData%");
            if (replaced != null) return replaced;
            replaced = ReplacePrefix(path, pf86, "%ProgramFiles(x86)%");
            if (replaced != null) return replaced;
            replaced = ReplacePrefix(path, pf, "%ProgramFiles%");
            if (replaced != null) return replaced;

            // Anything else under the user profile (Desktop, Documents, ...)
            // so the username is not written into generated configs.
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            replaced = ReplacePrefix(path, userProfile, "%USERPROFILE%");
            if (replaced != null) return replaced;

            return path;
        }

        private static string ReplacePrefix(string path, string prefix, string token)
        {
            if (string.IsNullOrEmpty(prefix)) return null;
            if (path.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return token;
            if (path.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
                return token + path.Substring(prefix.Length);
            return null;
        }

        public static List<string> NormalizePathList(IEnumerable<string> paths)
        {
            List<string> result = new List<string>();
            if (paths == null) return result;
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in paths)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                string p = Expand(raw);
                try { p = Path.GetFullPath(p).TrimEnd('\\'); }
                catch { continue; }
                if (seen.Add(p)) result.Add(p);
            }
            return result;
        }

        public static List<string> RemoveNestedPaths(List<string> paths)
        {
            List<string> sorted = new List<string>(paths);
            sorted.Sort(delegate(string a, string b)
            {
                int d = a.Length.CompareTo(b.Length);
                if (d != 0) return d;
                return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
            });

            // Keep the parent and drop any child: copying the parent already
            // contains everything under it. Keeping the deepest child (the old
            // behavior) silently dropped files that lived in the parent root.
            List<string> result = new List<string>();
            foreach (string path in sorted)
            {
                bool covered = false;
                foreach (string kept in result)
                {
                    if (IsSameOrSubPath(path, kept)) { covered = true; break; }
                }
                if (!covered) result.Add(path);
            }
            return result;
        }

        public static List<string> RemoveNestedRegistryKeys(List<string> keys)
        {
            List<string> sorted = new List<string>(keys);
            sorted.Sort(delegate(string a, string b)
            {
                int d = a.Count(c => c == '\\').CompareTo(b.Count(c => c == '\\'));
                if (d != 0) return d;
                return b.Length.CompareTo(a.Length);
            });

            List<string> result = new List<string>();
            foreach (string key in sorted)
            {
                bool covered = false;
                foreach (string kept in result)
                {
                    if (key.StartsWith(kept + "\\", StringComparison.OrdinalIgnoreCase)) { covered = true; break; }
                }
                if (!covered) result.Add(key);
            }
            return result;
        }

        public static bool SkipRegistryBranch(string name)
        {
            string n = Normalize(name);
            return n == "classes" || n == "microsoft" || n == "policies" ||
                   n == "registeredapplications" || n == "windows" || n == "clients";
        }

        public static bool IsRegistryContainer(string name)
        {
            string n = Normalize(name);
            return n == "wow6432node" || n == "software";
        }

        public static bool SkipRegistryKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return true;
            string k = key.ToLowerInvariant();
            return k.Contains("\\microsoft\\windows\\currentversion\\explorer")
                || k.Contains("\\microsoft\\windows\\currentversion\\search")
                || k.Contains("\\microsoft\\windows\\currentversion\\appmodel")
                || k.Contains("\\microsoft\\windows\\currentversion\\diagnostics")
                || k.Contains("\\microsoft\\windows\\currentversion\\winevt")
                || k.Contains("\\microsoft\\windows\\currentversion\\userassist")
                || k.Contains("\\microsoft\\windows\\currentversion\\runmru")
                || k.Contains("\\microsoft\\windows\\currentversion\\typedpaths")
                || k.Contains("\\microsoft\\windows\\currentversion\\recentdocs")
                || k.Contains("\\microsoft\\windows\\currentversion\\shell");
        }

        public static string NormalizeRegistryKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            string k = key.Trim();
            if (k.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase))
                return "HKEY_CURRENT_USER\\" + k.Substring(5);
            if (k.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase))
                return "HKEY_LOCAL_MACHINE\\" + k.Substring(5);
            if (k.StartsWith("HKCR\\", StringComparison.OrdinalIgnoreCase))
                return "HKEY_CLASSES_ROOT\\" + k.Substring(5);
            if (k.StartsWith("HKU\\", StringComparison.OrdinalIgnoreCase))
                return "HKEY_USERS\\" + k.Substring(4);
            if (string.Equals(k, "HKCU", StringComparison.OrdinalIgnoreCase)) return "HKEY_CURRENT_USER";
            if (string.Equals(k, "HKLM", StringComparison.OrdinalIgnoreCase)) return "HKEY_LOCAL_MACHINE";
            if (string.Equals(k, "HKCR", StringComparison.OrdinalIgnoreCase)) return "HKEY_CLASSES_ROOT";
            if (string.Equals(k, "HKU", StringComparison.OrdinalIgnoreCase)) return "HKEY_USERS";
            return k;
        }

        public static bool IsUninstallerName(string fileName)
        {
            string n = (fileName ?? "").ToLowerInvariant();
            return n.Contains("unins") || n.Contains("uninstall") || n.Contains("setup")
                || n.Contains("update") || n.Contains("updater") || n.Contains("crash")
                || n.Contains("reporter") || n.Contains("helper") || n.Contains("service")
                || n.Contains("elevation") || n.Contains("vc_redist") || n.Contains("dxsetup")
                || n.Contains("dotnet");
        }

        public static List<string> SafeEnumerateFiles(string root, string extension, int maxDepth)
        {
            List<string> result = new List<string>();
            if (!Directory.Exists(root)) return result;

            Stack<KeyValuePair<string, int>> stack = new Stack<KeyValuePair<string, int>>();
            stack.Push(new KeyValuePair<string, int>(root, 0));

            while (stack.Count > 0)
            {
                KeyValuePair<string, int> current = stack.Pop();
                try
                {
                    foreach (string file in Directory.EnumerateFiles(current.Key))
                    {
                        if (string.IsNullOrEmpty(extension) ||
                            file.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                            result.Add(file);
                    }

                    if (current.Value >= maxDepth) continue;
                    foreach (string dir in Directory.EnumerateDirectories(current.Key))
                    {
                        try
                        {
                            FileAttributes attr = File.GetAttributes(dir);
                            if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                        }
                        catch { continue; }
                        stack.Push(new KeyValuePair<string, int>(dir, current.Value + 1));
                    }
                }
                catch { }
            }
            return result;
        }

        public static int CopyTree(string source, string dest, Action<string> log)
        {
            return CopyTree(source, dest, log, null);
        }

        // Recursive delete that never follows reparse points (junctions/symlinks).
        public static void DeleteTreeSafe(string path)
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
                    try
                    {
                        FileAttributes fa = File.GetAttributes(file);
                        if ((fa & FileAttributes.ReadOnly) != 0) File.SetAttributes(file, fa & ~FileAttributes.ReadOnly);
                        File.Delete(file);
                    }
                    catch { }
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

        public static int CopyTree(string source, string dest, Action<string> log, List<string> excludes)
        {
            int failed;
            return CopyTree(source, dest, log, excludes, out failed);
        }

        public static int CopyTree(string source, string dest, Action<string> log, List<string> excludes, out int failed)
        {
            failed = 0;
            int count = 0;
            Stack<KeyValuePair<string, string>> stack = new Stack<KeyValuePair<string, string>>();
            stack.Push(new KeyValuePair<string, string>(source, dest));

            while (stack.Count > 0)
            {
                KeyValuePair<string, string> current = stack.Pop();
                try
                {
                    Directory.CreateDirectory(current.Value);

                    foreach (string file in Directory.EnumerateFiles(current.Key))
                    {
                        try
                        {
                            string rel = current.Value.Substring(dest.Length).TrimStart('\\');
                            rel = CombineRel(rel, Path.GetFileName(file));
                            if (IsExcluded(rel, excludes))
                            {
                                if (log != null) log("Skipped by exclude: " + rel);
                                continue;
                            }

                            FileAttributes attr = File.GetAttributes(file);
                            if ((attr & FileAttributes.ReparsePoint) != 0)
                            {
                                if (log != null) log("Skipped file link: " + file);
                                continue;
                            }
                            string target = Path.Combine(current.Value, Path.GetFileName(file));
                            ClearReadOnly(target);
                            File.Copy(file, target, true);
                            count++;
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            if (log != null) log("Copy failed: " + file + " : " + ex.Message);
                        }
                    }

                    foreach (string dir in Directory.EnumerateDirectories(current.Key))
                    {
                        try
                        {
                            string rel = current.Value.Substring(dest.Length).TrimStart('\\');
                            rel = CombineRel(rel, Path.GetFileName(dir));
                            if (IsExcluded(rel, excludes))
                            {
                                if (log != null) log("Skipped by exclude: " + rel);
                                continue;
                            }

                            FileAttributes attr = File.GetAttributes(dir);
                            if ((attr & FileAttributes.ReparsePoint) != 0)
                            {
                                if (log != null) log("Skipped folder link: " + dir);
                                continue;
                            }
                            stack.Push(new KeyValuePair<string, string>(dir,
                                Path.Combine(current.Value, Path.GetFileName(dir))));
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            if (log != null) log("Folder scan failed: " + dir + " : " + ex.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    if (log != null) log("Folder copy failed: " + current.Key + " : " + ex.Message);
                }
            }
            return count;
        }

        private static bool IsExcluded(string relativePath, List<string> excludes)
        {
            if (excludes == null || excludes.Count == 0) return false;

            string rel = (relativePath ?? "").Replace('/', '\\').Trim('\\').ToLowerInvariant();
            foreach (string raw in excludes)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                string ex = raw.Replace('/', '\\').Trim('\\').ToLowerInvariant();
                if (ex.Length == 0) continue;
                if (rel.Equals(ex, StringComparison.OrdinalIgnoreCase)) return true;
                if (rel.StartsWith(ex + "\\", StringComparison.OrdinalIgnoreCase)) return true;
                if (rel.IndexOf("\\" + ex + "\\", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static string CombineRel(string a, string b)
        {
            if (string.IsNullOrEmpty(a)) return b;
            return a.TrimEnd('\\') + "\\" + b;
        }

        public static void CopyOneFile(string source, string dest, Action<string> log)
        {
            try
            {
                string parent = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                ClearReadOnly(dest);
                File.Copy(source, dest, true);
            }
            catch (Exception ex)
            {
                if (log != null) log("File copy failed: " + source + " : " + ex.Message);
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

        public static string FindLauncher()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            string[] candidates =
            {
                Path.Combine(dir, "PortableLauncher.exe"),
                Path.Combine(dir, "..", "PortableLauncher", "PortableLauncher.exe"),
                Path.Combine(dir, "src", "PortableLauncher", "PortableLauncher.exe"),
                Path.Combine(dir, "..", "..", "PortableLauncher", "PortableLauncher.exe")
            };
            foreach (string candidate in candidates)
            {
                try
                {
                    string full = Path.GetFullPath(candidate);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
            return null;
        }

        public static bool ExtractEmbeddedLauncher(string destination)
        {
            return ExtractEmbeddedResource("PortableBuilder.PortableLauncher.exe", destination);
        }

        public static bool ExtractEmbeddedResource(string resourceName, string destination)
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream stream = asm.GetManifestResourceStream(resourceName))
                {
                    if (stream == null) return false;
                    string parent = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    using (FileStream file = File.Create(destination))
                        stream.CopyTo(file);
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
