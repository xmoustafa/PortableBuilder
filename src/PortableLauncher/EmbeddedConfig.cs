using System;
using System.Text;

namespace PortableLauncher
{
    /// <summary>
    /// Per-app builds replace this class with a generated one that holds the
    /// launcher.ini compressed + obfuscated, so the final EXE is self-contained
    /// and does not expose its config as plain text.
    /// </summary>
    internal static class EmbeddedConfig
    {
        public const string Base64 = "";

        public static string GetText()
        {
            if (string.IsNullOrEmpty(Base64)) return null;
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(Base64));
            }
            catch
            {
                return null;
            }
        }
    }
}
