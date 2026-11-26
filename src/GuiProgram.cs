using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace PortableBuilder
{
    internal static class GuiProgram
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // The downloaded new EXE runs in this mode to replace the old EXE
            // after the old process has exited.
            if (args != null && args.Length > 0 &&
                string.Equals(args[0], "--apply-update", StringComparison.OrdinalIgnoreCase))
            {
                Updater.ApplyUpdate(args);
                return;
            }

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                ReportCrash(e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                ReportCrash(e.ExceptionObject as Exception);
            };

            try
            {
                Application.Run(new BuilderForm());
            }
            catch (Exception ex)
            {
                ReportCrash(ex);
            }
        }

        private static void ReportCrash(Exception ex)
        {
            string logPath = null;
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagnostics");
                Directory.CreateDirectory(dir);
                logPath = Path.Combine(dir, "crash.log");
                File.AppendAllText(logPath,
                    DateTime.UtcNow.ToString("O") + Environment.NewLine +
                    (ex == null ? "Unknown error" : ex.ToString()) + Environment.NewLine + Environment.NewLine);
            }
            catch
            {
                // The app may be installed in a read-only/protected folder.
                try
                {
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "PortableBuilder", "diagnostics");
                    Directory.CreateDirectory(dir);
                    logPath = Path.Combine(dir, "crash.log");
                    File.AppendAllText(logPath,
                        DateTime.UtcNow.ToString("O") + Environment.NewLine +
                        (ex == null ? "Unknown error" : ex.ToString()) + Environment.NewLine + Environment.NewLine);
                }
                catch { logPath = null; }
            }

            try
            {
                string where = string.IsNullOrEmpty(logPath) ? "" : "\r\n\r\nLog: " + logPath;
                MessageBox.Show("The builder hit an unexpected error." + where + "\r\n\r\n" +
                    (ex == null ? "" : ex.Message), "PortableBuilder",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
    }
}
