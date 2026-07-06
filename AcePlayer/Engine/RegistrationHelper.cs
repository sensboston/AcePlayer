using System;
using Microsoft.Win32;

namespace AcePlayer.Engine
{
    /// <summary>
    /// Registers AcePlayer as the handler for acestream:// links and .acelive/.acestream files,
    /// under HKEY_CURRENT_USER (no administrator rights needed).
    /// </summary>
    internal static class RegistrationHelper
    {
        private const string ProgId = "AcePlayer.Stream";

        public static string ExePath =>
            System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;

        public static void Register()
        {
            string exe = ExePath;
            string open = $"\"{exe}\" \"%1\"";
            string icon = exe + ",0";

            // acestream:// protocol
            using (var proto = Registry.CurrentUser.CreateSubKey(@"Software\Classes\acestream"))
            {
                proto.SetValue("", "URL:Ace Stream Protocol");
                proto.SetValue("URL Protocol", "");
                using (var di = proto.CreateSubKey("DefaultIcon")) di.SetValue("", icon);
                using (var cmd = proto.CreateSubKey(@"shell\open\command")) cmd.SetValue("", open);
            }

            // ProgId used by the file extensions
            using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
            {
                prog.SetValue("", "Ace Stream");
                using (var di = prog.CreateSubKey("DefaultIcon")) di.SetValue("", icon);
                using (var cmd = prog.CreateSubKey(@"shell\open\command")) cmd.SetValue("", open);
            }

            foreach (var ext in new[] { ".acelive", ".acestream" })
                using (var e = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}"))
                    e.SetValue("", ProgId);
        }

        public static bool IsRegistered()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\acestream\shell\open\command"))
                    return k?.GetValue("") is string cmd && cmd.IndexOf("AcePlayer", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }
    }
}
