using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AcePlayer.Engine
{
    /// <summary>
    /// The (ad-supported) Ace Stream engine opens a betting-ad page in the user's default browser when
    /// a stream starts. The ad's title, domain and even the browser vary (it follows whatever the
    /// default browser is), so instead of matching brands we snapshot the existing browser windows just
    /// before the stream starts and close the first NEW browser window that appears right after — that
    /// is the ad. Browser- and brand-agnostic; only ever targets a window born in the watch window.
    /// </summary>
    internal static class AdWindowCloser
    {
        // Top-level window classes of the browsers that could host the ad.
        private static bool IsBrowserWindowClass(string cls) =>
            cls == "Chrome_WidgetWin_1"   // Chrome / Edge / any Chromium (incl. an embedded CEF)
            || cls == "MozillaWindowClass"; // Firefox

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder s, int max);
        [DllImport("user32.dll")] private static extern IntPtr PostMessageW(IntPtr h, uint msg, IntPtr w, IntPtr l);

        private const uint WM_CLOSE = 0x0010;

        /// <summary>
        /// Snapshot the current browser windows and, on a background thread, close the first new browser
        /// window that appears within <paramref name="seconds"/>. Call this just BEFORE the engine
        /// starts the stream (the ad opens on getstream), so the ad counts as "new".
        /// </summary>
        public static void CloseFor(double seconds = 8.0)
        {
            var baseline = SnapshotBrowserWindows();   // taken now, before the ad can open

            var t = new Thread(() =>
            {
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < seconds)
                {
                    IntPtr adWin = FindNewBrowserWindow(baseline);
                    if (adWin != IntPtr.Zero)
                    {
                        PostMessageW(adWin, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        Log($"closed new browser window: [{TitleOf(adWin)}]");
                        return;                        // the ad is a single window -> done
                    }
                    Thread.Sleep(120);
                }
                Log("no new browser window appeared");
            })
            { IsBackground = true, Name = "AdWindowCloser" };
            t.Start();
        }

        private static HashSet<IntPtr> SnapshotBrowserWindows()
        {
            var set = new HashSet<IntPtr>();
            EnumWindows((h, _) =>
            {
                if (IsWindowVisible(h) && IsBrowserWindowClass(ClassOf(h))) set.Add(h);
                return true;
            }, IntPtr.Zero);
            return set;
        }

        private static IntPtr FindNewBrowserWindow(HashSet<IntPtr> baseline)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((h, _) =>
            {
                if (found == IntPtr.Zero && IsWindowVisible(h) &&
                    IsBrowserWindowClass(ClassOf(h)) && !baseline.Contains(h))
                {
                    found = h;
                    return false;   // stop enumeration
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static string ClassOf(IntPtr h)
        {
            var sb = new StringBuilder(64);
            GetClassNameW(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string TitleOf(IntPtr h)
        {
            var sb = new StringBuilder(512);
            GetWindowTextW(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static void Log(string line)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "aceplayer_ads.log"),
                    $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
