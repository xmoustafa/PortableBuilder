using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace PortableBuilder
{
    internal sealed class ReleaseInfo
    {
        public Version Version;
        public string Tag;
        public string Notes;
        public string ExeUrl;
        public string ShaUrl;
        public string HtmlUrl;
    }

    /// <summary>
    /// GitHub Releases updater.
    ///
    /// The current EXE checks the latest release tag, downloads the new
    /// PortableBuilder.exe + its SHA-256, verifies it, then starts the new
    /// EXE in --apply-update mode. That helper waits for this process to exit,
    /// swaps the files and relaunches the app.
    /// </summary>
    internal static class Updater
    {
        private const int CheckIntervalHours = 6;

        public static void ScheduleCheck(Form owner)
        {
            if (!IsConfigured()) return;
            if (!ShouldCheckNow()) return;

            Task.Factory.StartNew(delegate
            {
                try
                {
                    // Let the main window finish painting and the scan start.
                    Thread.Sleep(3500);
                    ReleaseInfo release = FetchLatest();
                    WriteLastCheck();

                    if (release == null || release.Version == null) return;
                    if (release.Version <= CurrentVersion()) return;
                    if (IsSkipped(release.Tag)) return;

                    try
                    {
                        owner.BeginInvoke(new Action(delegate
                        {
                            if (owner.IsDisposed || owner.Disposing) return;
                            Prompt(owner, release);
                        }));
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    WriteLastCheck();
                    Log("Update check failed: " + ex.Message);
                }
            });
        }

        private static bool IsConfigured()
        {
            return !string.IsNullOrEmpty(UpdateConfig.Owner) &&
                   !string.IsNullOrEmpty(UpdateConfig.Repo) &&
                   UpdateConfig.Owner.IndexOf("__GITHUB_", StringComparison.Ordinal) < 0 &&
                   UpdateConfig.Repo.IndexOf("__GITHUB_", StringComparison.Ordinal) < 0;
        }

        private static Version CurrentVersion()
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            return version ?? new Version(1, 0, 0, 0);
        }

        private static ReleaseInfo FetchLatest()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            string url = "https://api.github.com/repos/" + Uri.EscapeDataString(UpdateConfig.Owner) +
                         "/" + Uri.EscapeDataString(UpdateConfig.Repo) + "/releases/latest";
            string json;
            using (WebClient client = new WebClient())
            {
                client.Headers[HttpRequestHeader.UserAgent] = "PortableBuilder-Updater";
                client.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                json = client.DownloadString(url);
            }

            JavaScriptSerializer serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            Dictionary<string, object> root = serializer.Deserialize<Dictionary<string, object>>(json);
            if (root == null) return null;

            string tag = GetString(root, "tag_name");
            Version version = ParseVersion(tag);
            if (version == null) return null;

            ReleaseInfo info = new ReleaseInfo();
            info.Tag = tag;
            info.Version = version;
            info.Notes = GetString(root, "body");
            info.HtmlUrl = GetString(root, "html_url");

            object assetsValue;
            if (root.TryGetValue("assets", out assetsValue) && assetsValue != null)
            {
                System.Collections.IEnumerable assets = assetsValue as System.Collections.IEnumerable;
                if (assets != null)
                {
                    string shaName = UpdateConfig.AssetName + UpdateConfig.ShaAssetSuffix;
                    foreach (object raw in assets)
                    {
                        Dictionary<string, object> asset = raw as Dictionary<string, object>;
                        if (asset == null) continue;
                        string name = GetString(asset, "name");
                        string download = GetString(asset, "browser_download_url");
                        if (string.Equals(name, UpdateConfig.AssetName, StringComparison.OrdinalIgnoreCase))
                            info.ExeUrl = download;
                        else if (string.Equals(name, shaName, StringComparison.OrdinalIgnoreCase))
                            info.ShaUrl = download;
                    }
                }
            }

            if (string.IsNullOrEmpty(info.ExeUrl)) return null;
            return info;
        }

        private static string GetString(Dictionary<string, object> dictionary, string key)
        {
            object value;
            return dictionary != null && dictionary.TryGetValue(key, out value) && value != null
                ? Convert.ToString(value)
                : "";
        }

        private static Version ParseVersion(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            string text = tag.Trim().TrimStart('v', 'V');
            int dash = text.IndexOf('-');
            if (dash > 0) text = text.Substring(0, dash);
            Version version;
            if (Version.TryParse(text, out version)) return version;
            return null;
        }

        private static void Prompt(Form owner, ReleaseInfo release)
        {
            string notes = (release.Notes ?? "").Trim();
            if (notes.Length > 1200) notes = notes.Substring(0, 1200) + "...";

            string text = "يوجد إصدار جديد من PortableBuilder: " + release.Tag + "\r\n" +
                          "الإصدار الحالي: " + CurrentVersion() + "\r\n\r\n" +
                          (notes.Length == 0 ? "" : notes + "\r\n\r\n") +
                          "هل تريد تحميل التحديث وتثبيته الآن؟\r\n" +
                          "سيتم إغلاق البرنامج ثم إعادة فتحه على الإصدار الجديد.";

            DialogResult answer = MessageBox.Show(owner, text, "تحديث PortableBuilder",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
            if (answer != DialogResult.Yes)
            {
                SaveSkipped(release.Tag);
                return;
            }

            DownloadAndApply(owner, release);
        }

        private static void DownloadAndApply(Form owner, ReleaseInfo release)
        {
            try
            {
                if (string.IsNullOrEmpty(release.ShaUrl))
                {
                    MessageBox.Show(owner,
                        "التحديث لا يحتوي على ملف تحقق SHA-256، لذلك تم إلغاؤه حفاظًا على الأمان.",
                        "تحديث PortableBuilder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string dir = Path.Combine(Path.GetTempPath(), "PortableBuilderUpdate");
                Directory.CreateDirectory(dir);
                string tempExe = Path.Combine(dir, "PortableBuilder.exe");
                string tempSha = Path.Combine(dir, "PortableBuilder.exe.sha256");
                try { if (File.Exists(tempExe)) File.Delete(tempExe); } catch { }
                try { if (File.Exists(tempSha)) File.Delete(tempSha); } catch { }

                bool downloaded;
                using (UpdateProgressForm form = new UpdateProgressForm(release.Tag))
                {
                    form.Shown += delegate
                    {
                        DownloadFiles(form, release, tempExe, tempSha);
                    };
                    downloaded = form.ShowDialog(owner) == DialogResult.OK && form.Success;
                    if (!downloaded)
                    {
                        if (!string.IsNullOrEmpty(form.Error))
                            MessageBox.Show(owner, "فشل تحميل التحديث:\r\n" + form.Error,
                                "تحديث PortableBuilder", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }

                if (!VerifySha256(tempExe, tempSha))
                {
                    try { File.Delete(tempExe); } catch { }
                    MessageBox.Show(owner,
                        "ملف التحديث لا يطابق بصمة SHA-256. تم إلغاء التحديث.",
                        "تحديث PortableBuilder", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                string target = Application.ExecutablePath;
                string arguments = "--apply-update " + Quote(target) + " " +
                                   Process.GetCurrentProcess().Id.ToString();

                ProcessStartInfo psi = new ProcessStartInfo(tempExe, arguments);
                psi.UseShellExecute = true;
                psi.WorkingDirectory = Path.GetDirectoryName(tempExe);
                Process.Start(psi);
                Application.Exit();
            }
            catch (Exception ex)
            {
                Log("Update apply start failed: " + ex);
                MessageBox.Show(owner, "تعذر بدء التحديث:\r\n" + ex.Message,
                    "تحديث PortableBuilder", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static WebClient NewClient()
        {
            WebClient client = new WebClient();
            client.Headers[HttpRequestHeader.UserAgent] = "PortableBuilder-Updater";
            return client;
        }

        private static void DownloadFiles(UpdateProgressForm form, ReleaseInfo release,
            string tempExe, string tempSha)
        {
            WebClient client = NewClient();
            client.DownloadProgressChanged += delegate(object sender, DownloadProgressChangedEventArgs e)
            {
                form.SetProgress(e.ProgressPercentage);
            };
            client.DownloadFileCompleted += delegate(object sender, AsyncCompletedEventArgs e)
            {
                try { client.Dispose(); } catch { }
                if (e.Error != null)
                {
                    form.Fail(e.Error.Message);
                    return;
                }
                if (e.Cancelled)
                {
                    form.Fail("تم إلغاء التحميل.");
                    return;
                }

                // The checksum file is tiny; fetching it in this callback is fine.
                try
                {
                    using (WebClient shaClient = NewClient())
                        shaClient.DownloadFile(new Uri(release.ShaUrl), tempSha);
                    form.Succeed();
                }
                catch (Exception ex)
                {
                    form.Fail("تعذر تحميل ملف التحقق: " + ex.Message);
                }
            };

            try
            {
                client.DownloadFileAsync(new Uri(release.ExeUrl), tempExe);
            }
            catch (Exception ex)
            {
                try { client.Dispose(); } catch { }
                form.Fail(ex.Message);
            }
        }

        private static bool VerifySha256(string file, string shaFile)
        {
            try
            {
                string expected = File.ReadAllText(shaFile).Trim();
                if (expected.Length == 0) return false;
                int space = expected.IndexOfAny(new char[] { ' ', '\t', '\r', '\n' });
                if (space > 0) expected = expected.Substring(0, space);
                expected = expected.Trim().ToLowerInvariant();

                using (SHA256 sha = SHA256.Create())
                using (FileStream stream = File.OpenRead(file))
                {
                    StringBuilder actual = new StringBuilder();
                    foreach (byte b in sha.ComputeHash(stream)) actual.Append(b.ToString("x2"));
                    return string.Equals(actual.ToString(), expected, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        public static void ApplyUpdate(string[] args)
        {
            try
            {
                if (args == null || args.Length < 3)
                {
                    Fail("Invalid update arguments.");
                    return;
                }

                string target;
                int pid;
                try { target = Path.GetFullPath(args[1]); }
                catch { Fail("Invalid target path."); return; }
                if (!int.TryParse(args[2], out pid)) pid = 0;

                if (!File.Exists(target))
                {
                    Fail("The installed application was not found:\r\n" + target);
                    return;
                }

                if (pid > 0)
                {
                    try
                    {
                        using (Process old = Process.GetProcessById(pid))
                        {
                            if (!old.WaitForExit(30000))
                            {
                                Fail("The old application is still running. Close it and run the app again.");
                                return;
                            }
                        }
                    }
                    catch { }
                }

                string self = Application.ExecutablePath;
                string backup = target + ".update-old";
                try
                {
                    if (File.Exists(backup)) File.Delete(backup);
                    ClearReadOnly(target);
                    File.Move(target, backup);
                    try
                    {
                        File.Copy(self, target, true);
                    }
                    catch
                    {
                        try { if (File.Exists(backup) && !File.Exists(target)) File.Move(backup, target); }
                        catch { }
                        throw;
                    }

                    ProcessStartInfo psi = new ProcessStartInfo(target);
                    psi.UseShellExecute = true;
                    psi.WorkingDirectory = Path.GetDirectoryName(target);
                    Process.Start(psi);

                    try { File.Delete(backup); } catch { }
                    try { MoveFileEx(self, null, MOVEFILE_DELAY_UNTIL_REBOOT); } catch { }
                    Log("Update installed: " + target);
                }
                catch (UnauthorizedAccessException ex)
                {
                    if (!IsElevated())
                    {
                        try
                        {
                            ProcessStartInfo elevated = new ProcessStartInfo(self,
                                "--apply-update " + Quote(target) + " " + pid.ToString());
                            elevated.UseShellExecute = true;
                            elevated.Verb = "runas";
                            Process.Start(elevated);
                            return;
                        }
                        catch (Exception runEx)
                        {
                            Fail("Administrator rights are required to update:\r\n" + runEx.Message);
                            return;
                        }
                    }
                    Fail("Access denied while updating:\r\n" + ex.Message);
                }
            }
            catch (Exception ex)
            {
                Fail("Update failed:\r\n" + ex.Message);
            }
        }

        private static void ClearReadOnly(string path)
        {
            try
            {
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
            catch { }
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

        private static void Fail(string message)
        {
            try
            {
                MessageBox.Show(message, "PortableBuilder Update",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }

        private static bool ShouldCheckNow()
        {
            try
            {
                string file = StatePath("last-check.txt");
                if (!File.Exists(file)) return true;
                DateTime last;
                if (!DateTime.TryParse(File.ReadAllText(file).Trim(), out last)) return true;
                return DateTime.UtcNow - last >= TimeSpan.FromHours(CheckIntervalHours);
            }
            catch { return true; }
        }

        private static void WriteLastCheck()
        {
            try
            {
                string file = StatePath("last-check.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, DateTime.UtcNow.ToString("O"));
            }
            catch { }
        }

        private static bool IsSkipped(string tag)
        {
            try
            {
                string file = StatePath("skipped.txt");
                return File.Exists(file) &&
                       string.Equals(File.ReadAllText(file).Trim(), tag, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void SaveSkipped(string tag)
        {
            try
            {
                string file = StatePath("skipped.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllText(file, tag ?? "");
            }
            catch { }
        }

        private static string StatePath(string name)
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PortableBuilder", name);
        }

        private static void Log(string message)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "PortableBuilder");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "updater.log"),
                    DateTime.UtcNow.ToString("O") + "  " + message + Environment.NewLine);
            }
            catch { }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);

        private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;
    }

    /// <summary>Small modal download progress window.</summary>
    internal sealed class UpdateProgressForm : Form
    {
        private readonly ProgressBar _bar;
        private readonly Label _status;
        public bool Success;
        public string Error;

        public UpdateProgressForm(string tag)
        {
            Text = "تحديث PortableBuilder " + tag;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            ClientSize = new Size(460, 130);
            Font = new Font("Segoe UI", 9F);
            RightToLeft = RightToLeft.Yes;

            Label title = new Label();
            title.Text = "جاري تحميل التحديث...";
            title.SetBounds(16, 14, 428, 22);
            title.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            Controls.Add(title);

            _bar = new ProgressBar();
            _bar.SetBounds(16, 48, 428, 22);
            _bar.Minimum = 0;
            _bar.Maximum = 100;
            Controls.Add(_bar);

            _status = new Label();
            _status.Text = "0%";
            _status.SetBounds(16, 80, 428, 22);
            _status.ForeColor = Color.DimGray;
            Controls.Add(_status);
        }

        public void SetProgress(int percent)
        {
            if (IsDisposed) return;
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            _bar.Value = percent;
            _status.Text = percent + "%";
        }

        public void Succeed()
        {
            if (IsDisposed) return;
            Success = true;
            DialogResult = DialogResult.OK;
            Close();
        }

        public void Fail(string message)
        {
            if (IsDisposed) return;
            Success = false;
            Error = message;
            DialogResult = DialogResult.Abort;
            Close();
        }
    }
}
